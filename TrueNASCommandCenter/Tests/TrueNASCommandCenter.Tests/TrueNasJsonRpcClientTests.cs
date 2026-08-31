using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

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
    public async Task ApiKeyAuthentication_FallsBackToLegacyShapeOnInvalidParams()
    {
        var setup = await TestClientFactory.CreateAsync();
        var authCalls = 0;
        var includedUsername = new List<bool>();
        var includedReconnectToken = new List<bool>();
        setup.Transport.OnSend = request =>
        {
            var method = request.GetProperty("method").GetString();
            var id = request.GetProperty("id").GetInt64();
            if (method == "auth.login_ex")
            {
                authCalls++;
                var login = request.GetProperty("params")[0];
                includedUsername.Add(login.TryGetProperty("username", out _));
                includedReconnectToken.Add(login.GetProperty("login_options").TryGetProperty("reconnect_token", out _));
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
        CollectionAssert.AreEqual(new[] { false, false }, includedReconnectToken);
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
        Assert.IsNotNull(result.DiagnosticId);
        StringAssert.Contains(result.Message, result.DiagnosticId);
    }

    [TestMethod]
    public async Task ConnectionTest_WithAppRolesDoesNotRequireAnAdditionalMailRole()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var id = request.GetProperty("id").GetInt64();
            switch (request.GetProperty("method").GetString())
            {
                case "core.ping":
                    transport.Respond(id, "pong");
                    break;
                case "app.query":
                    transport.Respond(id, Array.Empty<object>());
                    break;
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var result = await client.TestConnectionAsync();

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.HasReadAccess);
        Assert.IsTrue(result.HasWriteAccess);
        Assert.AreEqual("Connected and app discovery succeeded.", result.Message);
    }

    [TestMethod]
    public async Task ConnectionTest_LogsDiagnosticIdWithoutApiKey()
    {
        var logger = new RecordingLogger<TrueNasJsonRpcClient>();
        var setup = await TestClientFactory.CreateAsync(logger: logger);
        setup.Transport.ConnectException = new WebSocketException(
            WebSocketError.ConnectionClosedPrematurely,
            "The test WebSocket connection failed.");
        await using var client = setup.Client;
        await using var database = setup.Database;

        var result = await client.TestConnectionAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual("NETWORK_ERROR", result.ErrorCode);
        Assert.IsNotNull(result.DiagnosticId);
        StringAssert.Contains(result.Message, result.DiagnosticId);
        Assert.IsTrue(logger.Entries.Any(entry => entry.Message.Contains(result.DiagnosticId, StringComparison.Ordinal)));
        Assert.IsFalse(logger.Entries.Any(entry =>
            entry.Message.Contains("test-api-key", StringComparison.Ordinal) ||
            (entry.Exception?.ToString().Contains("test-api-key", StringComparison.Ordinal) ?? false)));
    }

    /// <summary>Verifies that unreachable host and network failures return actionable host-network guidance.</summary>
    /// <param name="socketErrorCode">The native socket error to classify.</param>
    [TestMethod]
    [DataRow((int)SocketError.HostUnreachable)]
    [DataRow((int)SocketError.NetworkUnreachable)]
    [TestCategory("Unit")]
    public async Task ConnectionTest_UnreachableTrueNasHost_ReturnsActionableNetworkError(int socketErrorCode)
    {
        // Arrange
        var setup = await TestClientFactory.CreateAsync();
        setup.Transport.ConnectException = new WebSocketException(
            WebSocketError.Faulted,
            "Unable to connect to the remote server.",
            new SocketException(socketErrorCode));
        await using var client = setup.Client;
        await using var database = setup.Database;

        // Act
        var result = await client.TestConnectionAsync();

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual("NETWORK_UNREACHABLE", result.ErrorCode);
        StringAssert.Contains(result.Message, "Host Network mode");
        StringAssert.Contains(result.Message, "TRUENAS_WEBSOCKET_URL");
    }

    /// <summary>Verifies that hostname resolution failures explain the deployment configuration recovery path.</summary>
    /// <param name="socketErrorCode">The native DNS socket error to classify.</param>
    [TestMethod]
    [DataRow((int)SocketError.HostNotFound)]
    [DataRow((int)SocketError.NoData)]
    [DataRow((int)SocketError.TryAgain)]
    [TestCategory("Unit")]
    public async Task ConnectionTest_UnresolvableTrueNasHost_ReturnsActionableDnsError(int socketErrorCode)
    {
        // Arrange
        var setup = await TestClientFactory.CreateAsync();
        setup.Transport.ConnectException = new WebSocketException(
            WebSocketError.Faulted,
            "Unable to connect to the remote server.",
            new SocketException(socketErrorCode));
        await using var client = setup.Client;
        await using var database = setup.Database;

        // Act
        var result = await client.TestConnectionAsync();

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual("DNS_FAILURE", result.ErrorCode);
        StringAssert.Contains(result.Message, "TRUENAS_WEBSOCKET_URL");
        StringAssert.Contains(result.Message, "extra_hosts");
    }

    [TestMethod]
    public async Task JobFailure_UsesCoreGetJobsDiagnosticFallback()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var id = request.GetProperty("id").GetInt64();
            switch (request.GetProperty("method").GetString())
            {
                case "core.job_wait":
                    transport.Error(id, -32001, "Job failed");
                    break;
                case "core.get_jobs":
                    transport.Respond(id, new { id = 42, state = "FAILED", error = "Image pull failed" });
                    break;
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var exception = await Assert.ThrowsAsync<TrueNasClientException>(() => client.WaitForJobAsync(42));

        StringAssert.Contains(exception.Message, "TrueNAS job FAILED");
        StringAssert.Contains(exception.Message, "Image pull failed");
    }

    [TestMethod]
    public async Task ResetConnection_ReconnectsCleanly()
    {
        await using var database = new TestDatabase();
        var protector = database.CreateProtector();
        await database.InitializeAsync(settings =>
        {
            settings.TrueNasUsername = "service";
            settings.TrueNasApiKeyEncrypted = protector.Protect("test-key");
        });
        var first = TransportReturning("first");
        var second = TransportReturning("second");
        await using var client = new TrueNasJsonRpcClient(
            new SequenceWebSocketTransportFactory(first, second),
            new SettingsService(database, protector, TestDatabase.TrueNasEndpoint),
            database,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            NullLogger<TrueNasJsonRpcClient>.Instance);

        var firstResult = await client.QueryAppsAsync();
        await client.ResetConnectionAsync();
        var secondResult = await client.QueryAppsAsync();

        Assert.AreEqual("first", firstResult.Single().Id);
        Assert.AreEqual("second", secondResult.Single().Id);
    }

    /// <summary>Verifies that app lifecycle operations use the expected TrueNAS job methods.</summary>
    /// <param name="action">The lifecycle action under test.</param>
    /// <param name="expectedMethod">The expected TrueNAS JSON-RPC method.</param>
    [TestMethod]
    [DataRow("start", "app.start")]
    [DataRow("stop", "app.stop")]
    public async Task AppLifecycleActions_SendExpectedRpcMethod(string action, string expectedMethod)
    {
        string? capturedMethod = null;
        string? capturedAppId = null;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            capturedMethod = request.GetProperty("method").GetString();
            capturedAppId = request.GetProperty("params")[0].GetString();
            transport.Respond(request.GetProperty("id").GetInt64(), 84L);
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var jobId = action switch
        {
            "start" => await client.StartAppAsync("immich"),
            "stop" => await client.StopAppAsync("immich"),
            _ => throw new InvalidOperationException("Unsupported test action.")
        };

        Assert.AreEqual(84L, jobId);
        Assert.AreEqual(expectedMethod, capturedMethod);
        Assert.AreEqual("immich", capturedAppId);
    }

    [TestMethod]
    public async Task SendMail_UsesTrueNasMailJobAndOptionalRecipients()
    {
        JsonElement? capturedPayload = null;
        var waitedForJob = false;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var id = request.GetProperty("id").GetInt64();
            switch (request.GetProperty("method").GetString())
            {
                case "mail.send":
                    capturedPayload = request.GetProperty("params")[0].Clone();
                    transport.Respond(id, 73L);
                    break;
                case "core.job_wait":
                    waitedForJob = request.GetProperty("params")[0].GetInt64() == 73L;
                    transport.Respond(id, new { });
                    break;
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        await client.SendMailAsync(new TrueNasMailMessage("Subject", "Body", ["admin@example.test"]));

        Assert.IsNotNull(capturedPayload);
        Assert.AreEqual("Subject", capturedPayload.Value.GetProperty("subject").GetString());
        Assert.AreEqual("admin@example.test", capturedPayload.Value.GetProperty("to")[0].GetString());
        Assert.IsTrue(waitedForJob);
    }

    [TestMethod]
    public async Task FollowContainerLogs_RoutesSubscriptionEventsAndUnsubscribes()
    {
        string? eventName = null;
        string? unsubscribed = null;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var id = request.GetProperty("id").GetInt64();
            switch (request.GetProperty("method").GetString())
            {
                case "core.subscribe":
                    eventName = request.GetProperty("params")[0].GetString();
                    transport.Respond(id, "subscription-1");
                    transport.Push(new
                    {
                        jsonrpc = "2.0",
                        method = "collection_update",
                        @params = new
                        {
                            collection = eventName,
                            fields = new { data = "container ready", timestamp = "2026-08-22T21:00:00Z", stream = "stderr" }
                        }
                    });
                    break;
                case "core.unsubscribe":
                    unsubscribed = request.GetProperty("params")[0].GetString();
                    transport.Respond(id, true);
                    break;
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;
        TrueNasLogEntry? received = null;

        await foreach (var entry in client.FollowContainerLogsAsync(new TrueNasContainerLogRequest("immich", "container-1")))
        {
            received = entry;
            break;
        }

        Assert.IsNotNull(received);
        StringAssert.Contains(eventName ?? string.Empty, "app.container_log_follow:");
        StringAssert.Contains(eventName ?? string.Empty, "immich");
        Assert.AreEqual("container ready", received.Message);
        Assert.AreEqual("stderr", received.Stream);
        Assert.AreEqual("subscription-1", unsubscribed);
    }

    /// <summary>Verifies that host identity, hardware, and uptime deserialize from the expected RPC method.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetSystemInfo_ReturnsHostIdentityAndRuntimeInformation()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            Assert.AreEqual("system.info", request.GetProperty("method").GetString());
            Assert.AreEqual(0, request.GetProperty("params").GetArrayLength());
            transport.Respond(request.GetProperty("id").GetInt64(), new
            {
                version = "25.10.1",
                hostname = "atlas",
                physmem = 68_719_476_736L,
                model = "Example CPU",
                cores = 16,
                physical_cores = 8,
                loadavg = new[] { 0.25, 0.5, 0.75 },
                uptime = "4 days, 2 hours",
                uptime_seconds = 352_800d,
                boottime = "2026-08-23T16:00:00Z",
                timezone = "America/Los_Angeles",
                system_manufacturer = "Example Systems",
                system_product = "Storage Server",
                ecc_memory = true
            });
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var host = await client.GetSystemInfoAsync();

        Assert.AreEqual("atlas", host.Hostname);
        Assert.AreEqual("25.10.1", host.Version);
        Assert.AreEqual(68_719_476_736L, host.PhysicalMemory);
        Assert.AreEqual(16, host.CoreCount);
        Assert.HasCount(3, host.LoadAverage);
        Assert.AreEqual(0.5, host.LoadAverage[1]);
        Assert.IsTrue(host.HasEccMemory);
    }

    /// <summary>Verifies that active and dismissed system alerts deserialize from the expected RPC method.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task ListAlerts_ReturnsSeverityAndOccurrenceInformation()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            Assert.AreEqual("alert.list", request.GetProperty("method").GetString());
            Assert.AreEqual(0, request.GetProperty("params").GetArrayLength());
            transport.Respond(request.GetProperty("id").GetInt64(), new[]
            {
                new
                {
                    uuid = "alert-1",
                    id = "Smartd",
                    source = "Disk",
                    klass = "Smartd",
                    node = "atlas",
                    datetime = "2026-08-27T17:00:00Z",
                    last_occurrence = "2026-08-27T18:00:00Z",
                    dismissed = false,
                    text = "Disk fault detected",
                    level = "CRITICAL",
                    one_shot = true
                }
            });
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var alerts = await client.ListAlertsAsync();

        Assert.HasCount(1, alerts);
        Assert.AreEqual("alert-1", alerts[0].Uuid);
        Assert.AreEqual("CRITICAL", alerts[0].Level);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.Zero), alerts[0].LastOccurrence);
        Assert.IsTrue(alerts[0].IsOneShot);
    }

    /// <summary>Verifies that TrueNAS operating-system update availability deserializes from the expected RPC method.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetUpdateStatus_ReturnsAvailableVersionAndDownloadProgress()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            Assert.AreEqual("update.status", request.GetProperty("method").GetString());
            Assert.AreEqual(0, request.GetProperty("params").GetArrayLength());
            transport.Respond(request.GetProperty("id").GetInt64(), new
            {
                code = "NORMAL",
                status = new
                {
                    current_version = new { train = "TrueNAS-SCALE-25.10", profile = "GENERAL", matches_profile = true },
                    new_version = new { version = "25.10.2", release_notes = "Reliability update", release_notes_url = "https://www.truenas.com/docs/release-notes/" }
                },
                error = (object?)null,
                update_download_progress = new { percent = 35.5, description = "Downloading update", version = "25.10.2" }
            });
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var update = await client.GetUpdateStatusAsync();

        Assert.AreEqual("NORMAL", update.Code);
        Assert.AreEqual("TrueNAS-SCALE-25.10", update.Status!.CurrentVersion!.Train);
        Assert.AreEqual("25.10.2", update.Status.NewVersion!.Version);
        Assert.AreEqual(35.5, update.DownloadProgress!.Percent);
    }

    /// <summary>Verifies that storage-pool health and capacity deserialize from the expected RPC method.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task QueryPools_ReturnsPoolHealthAndCapacity()
    {
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            Assert.AreEqual("pool.query", request.GetProperty("method").GetString());
            transport.Respond(request.GetProperty("id").GetInt64(), new[]
            {
                new { name = "tank", status = "ONLINE", healthy = true, warning = false, size = 1_000L, allocated = 400L, free = 600L, fragmentation = "4%", scan = new { function = "SCRUB", state = "SCANNING", start_time = "2026-08-30T17:00:00Z", end_time = (object?)null, percentage = 35.5, errors = 0, total_secs_left = 900 }, topology = new { data = new[] { new { name = "sda", type = "DISK", status = "ONLINE", disk = "sda", stats = new { read_errors = 1L, write_errors = 0L, checksum_errors = 0L }, children = Array.Empty<object>() } }, log = Array.Empty<object>(), cache = Array.Empty<object>(), spare = Array.Empty<object>(), special = Array.Empty<object>(), dedup = Array.Empty<object>() } }
            });
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var pools = await client.QueryPoolsAsync();
        Assert.HasCount(1, pools);
        var pool = pools[0];

        Assert.AreEqual("tank", pool.Name);
        Assert.IsTrue(pool.Healthy);
        Assert.AreEqual(400L, pool.Allocated);
        Assert.AreEqual("4%", pool.Fragmentation);
        Assert.AreEqual("SCRUB", pool.Scan!.Function);
        Assert.AreEqual(35.5, pool.Scan.Percentage);
        Assert.HasCount(1, pool.Topology!.Data);
        Assert.AreEqual("sda", pool.Topology.Data[0].Disk);
        Assert.AreEqual(1L, pool.Topology.Data[0].Stats!.ReadErrors);
    }

    /// <summary>Verifies the data-protection client calls the five focused TrueNAS query methods.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task DataProtectionQueries_SendExpectedRpcMethods()
    {
        var methods = new List<string>();
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            methods.Add(request.GetProperty("method").GetString()!);
            transport.Respond(request.GetProperty("id").GetInt64(), Array.Empty<object>());
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        await client.QueryDatasetsAsync();
        await client.QuerySnapshotsAsync();
        await client.QuerySnapshotTasksAsync();
        await client.QueryReplicationTasksAsync();
        await client.QueryCloudSyncTasksAsync();

        CollectionAssert.AreEqual(new[] { "pool.dataset.query", "pool.snapshot.query", "pool.snapshottask.query", "replication.query", "cloudsync.query" }, methods);
    }

    /// <summary>Verifies disk identity and temperature requests use the expected methods and include thresholds.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task DriveHealthQueries_SendDiskNamesAndRequestThresholds()
    {
        JsonElement? temperatureParameters = null;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var method = request.GetProperty("method").GetString();
            if (method == "disk.query")
            {
                transport.Respond(request.GetProperty("id").GetInt64(), Array.Empty<object>());
            }
            else if (method == "disk.temperatures")
            {
                temperatureParameters = request.GetProperty("params").Clone();
                transport.Respond(request.GetProperty("id").GetInt64(), new { sda = new { temperature = 37, critical = 70 } });
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var disks = await client.QueryDisksAsync();
        var temperatures = await client.GetDiskTemperaturesAsync(["sda"]);

        Assert.IsEmpty(disks);
        Assert.IsNotNull(temperatureParameters);
        Assert.AreEqual("sda", temperatureParameters.Value[0][0].GetString());
        Assert.IsTrue(temperatureParameters.Value[1].GetBoolean());
        Assert.AreEqual(37, temperatures["sda"].GetProperty("temperature").GetInt32());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ListJobs_RequestsNewestJobsAndDeserializesProgress()
    {
        JsonElement? capturedParameters = null;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            Assert.AreEqual("core.get_jobs", request.GetProperty("method").GetString());
            capturedParameters = request.GetProperty("params").Clone();
            transport.Respond(request.GetProperty("id").GetInt64(), new[]
            {
                new { id = 91L, method = "app.upgrade", arguments = new[] { "plex" }, description = "Upgrade Plex", progress = new { percent = 64.0, description = "Pulling images" }, error = (string?)null, state = "RUNNING", time_started = "2026-08-30T17:00:00Z", time_finished = (object?)null }
            });
            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;

        var jobs = await client.ListJobsAsync(75);

        Assert.HasCount(1, jobs);
        Assert.AreEqual(91L, jobs[0].Id);
        Assert.AreEqual("RUNNING", jobs[0].State);
        Assert.AreEqual(64.0, jobs[0].Progress!.Percent);
        Assert.IsNotNull(capturedParameters);
        Assert.AreEqual("-id", capturedParameters.Value[1].GetProperty("order_by")[0].GetString());
        Assert.AreEqual(75, capturedParameters.Value[1].GetProperty("limit").GetInt32());
    }

    /// <summary>Verifies that app statistics events route to the stream and release their subscription.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task WatchAppStats_RoutesResourceEventsAndUnsubscribes()
    {
        string? eventName = null;
        string? unsubscribed = null;
        var setup = await TestClientFactory.CreateAsync((transport, request) =>
        {
            var id = request.GetProperty("id").GetInt64();
            switch (request.GetProperty("method").GetString())
            {
                case "core.subscribe":
                    eventName = request.GetProperty("params")[0].GetString();
                    transport.Respond(id, "stats-subscription");
                    transport.Push(new
                    {
                        jsonrpc = "2.0",
                        method = "collection_update",
                        @params = new
                        {
                            collection = eventName,
                            fields = new[]
                            {
                                new
                                {
                                    app_name = "immich",
                                    cpu_usage = 17,
                                    memory = 268_435_456L,
                                    networks = new[] { new { interface_name = "eth0", rx_bytes = 1024L, tx_bytes = 2048L } },
                                    blkio = new { read = 4096L, write = 8192L }
                                }
                            }
                        }
                    });
                    break;
                case "core.unsubscribe":
                    unsubscribed = request.GetProperty("params")[0].GetString();
                    transport.Respond(id, true);
                    break;
            }

            return Task.CompletedTask;
        });
        await using var client = setup.Client;
        await using var database = setup.Database;
        TrueNasAppStatsDto? received = null;

        await foreach (var batch in client.WatchAppStatsAsync())
        {
            Assert.HasCount(1, batch);
            received = batch[0];
            break;
        }

        Assert.IsNotNull(received);
        StringAssert.StartsWith(eventName ?? string.Empty, "app.stats:");
        StringAssert.Contains(eventName ?? string.Empty, "\"interval\":5");
        Assert.AreEqual("immich", received.AppName);
        Assert.AreEqual(17, received.CpuUsage);
        Assert.AreEqual(268_435_456L, received.Memory);
        Assert.HasCount(1, received.Networks);
        Assert.AreEqual(1024L, received.Networks[0].ReceiveBytes);
        Assert.AreEqual(8192L, received.BlockIo.WriteBytes);
        Assert.AreEqual("stats-subscription", unsubscribed);
    }

    private static FakeWebSocketTransport TransportReturning(string appId)
    {
        var transport = new FakeWebSocketTransport();
        transport.OnSend = request =>
        {
            var id = request.GetProperty("id").GetInt64();
            if (request.GetProperty("method").GetString() == "auth.login_ex")
            {
                transport.Respond(id, new { response_type = "SUCCESS", user_info = (object?)null });
            }
            else
            {
                transport.Respond(id, new[] { App(appId) });
            }

            return Task.CompletedTask;
        };
        return transport;
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
