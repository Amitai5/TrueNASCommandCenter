using System.Text.Json;
using TrueNasUpdateManager.Integrations.TrueNas;

namespace TrueNasUpdateManager.Tests;

[TestClass]
public sealed class TrueNasJsonRpcClientTests
{
    [TestMethod]
    public async Task ConcurrentCalls_AreCorrelatedWhenResponsesArriveOutOfOrder()
    {
        long firstId = 0;
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryCount = 0;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            if (request.GetProperty("method").GetString() != "app.query")
            {
                return Task.CompletedTask;
            }

            var id = request.GetProperty("id").GetInt64();
            queryCount++;
            if (queryCount == 1)
            {
                firstId = id;
                firstSent.SetResult();
            }
            else
            {
                transport.Respond(id, new[] { App("second") });
                transport.Respond(firstId, new[] { App("first") });
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var first = client.QueryAppsAsync();
        await firstSent.Task;
        var second = client.QueryAppsAsync();
        await Task.WhenAll(first, second);

        Assert.AreEqual("first", first.Result.Single().Id);
        Assert.AreEqual("second", second.Result.Single().Id);
    }

    [TestMethod]
    public async Task RpcErrors_AreTypedAndSanitized()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            transport.Error(request.GetProperty("id").GetInt64(), -32001, "Permission denied");
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var exception = await Assert.ThrowsAsync<TrueNasClientException>(() => client.QueryAppsAsync());

        Assert.AreEqual("-32001", exception.Code);
        Assert.AreEqual("Permission denied", exception.Message);
    }

    [TestMethod]
    public async Task ApiKeyAuthentication_FallsBackTo25_10ShapeOnInvalidParams()
    {
        var setup = await TestClientFactory.CreateAsync();
        var authCalls = 0;
        var includedUsername = new List<bool>();
        setup.Transport.OnSend = request =>
        {
            var method = request.GetProperty("method").GetString();
            var id = request.GetProperty("id").GetInt64();
            if (method == "auth.login_ex")
            {
                authCalls++;
                var login = request.GetProperty("params")[0];
                includedUsername.Add(login.TryGetProperty("username", out _));
                if (authCalls == 1)
                {
                    setup.Transport.Error(id, -32602, "Invalid parameters");
                }
                else
                {
                    setup.Transport.Respond(id, new { response_type = "SUCCESS", user_info = (object?)null });
                }
            }
            else if (method == "app.query")
            {
                setup.Transport.Respond(id, Array.Empty<object>());
            }

            return Task.CompletedTask;
        };
        await using var client = setup.Client;
        await using var database = setup.Database;

        var apps = await client.QueryAppsAsync();

        Assert.IsEmpty(apps);
        CollectionAssert.AreEqual(new[] { true, false }, includedUsername);
    }

    [TestMethod]
    public async Task Cancellation_StopsPendingCall()
    {
        var querySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = await TestClientFactory.CreateAsync((_, request) =>
        {
            if (request.GetProperty("method").GetString() == "app.query")
            {
                querySent.SetResult();
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;
        using var cancellation = new CancellationTokenSource();

        var call = client.QueryAppsAsync(cancellation.Token);
        await querySent.Task;
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await call);
    }

    [TestMethod]
    public async Task ConnectionTest_DistinguishesAuthenticationFailure()
    {
        var setup = await TestClientFactory.CreateAsync();
        setup.Transport.OnSend = request =>
        {
            setup.Transport.Respond(
                request.GetProperty("id").GetInt64(),
                new { response_type = "AUTH_ERR", user_info = (object?)null });
            return Task.CompletedTask;
        };
        await using var client = setup.Client;
        await using var database = setup.Database;

        var result = await client.TestConnectionAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual("AUTHENTICATION_FAILED", result.ErrorCode);
    }

    private static object App(string id) => new
    {
        id,
        name = id,
        state = "RUNNING",
        upgrade_available = false,
        image_updates_available = false,
        custom_app = false,
        human_version = "1.0.0",
        version = "1.0.0",
        action_required = false
    };
}
