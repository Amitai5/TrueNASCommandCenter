using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class TrueNasServerAddressServiceTests
{
    [TestMethod]
    public async Task GetAsync_LiteralIpv4Address_ReturnsAddressWithoutDnsLookup()
    {
        var resolver = new FakeHostAddressResolver([IPAddress.Parse("192.168.1.20")]);
        var service = CreateService("wss://10.0.0.21/api/current", resolver);

        var result = await service.GetAsync();

        Assert.AreEqual("10.0.0.21", result.HostName);
        Assert.AreEqual("10.0.0.21", result.IpAddress);
        Assert.IsTrue(result.IsResolved);
        Assert.IsFalse(result.HasDistinctHostName);
        Assert.AreEqual("http://10.0.0.21/", result.WebUiUrl);
        Assert.AreEqual(0, resolver.CallCount);
    }

    [TestMethod]
    public async Task GetAsync_HostnameWithIpv4AndIpv6_PrefersIpv4Address()
    {
        var resolver = new FakeHostAddressResolver([IPAddress.Parse("fd00::21"), IPAddress.Parse("10.0.0.21")]);
        var service = CreateService("wss://truenas.local/api/current", resolver);

        var result = await service.GetAsync();

        Assert.AreEqual("truenas.local", result.HostName);
        Assert.AreEqual("10.0.0.21", result.IpAddress);
        Assert.IsTrue(result.HasDistinctHostName);
        Assert.AreEqual("http://truenas.local/", result.WebUiUrl);
        Assert.AreEqual(1, resolver.CallCount);
    }

    [TestMethod]
    public async Task GetAsync_UnresolvableHostname_ReturnsExplicitUnavailableResult()
    {
        var resolver = new FakeHostAddressResolver(new SocketException((int)SocketError.HostNotFound));
        var service = CreateService("wss://truenas.local/api/current", resolver);

        var result = await service.GetAsync();

        Assert.AreEqual("truenas.local", result.HostName);
        Assert.IsNull(result.IpAddress);
        Assert.IsFalse(result.IsResolved);
        Assert.IsTrue(result.HasDistinctHostName);
        Assert.AreEqual("IP unavailable", result.DisplayAddress);
        Assert.AreEqual("http://truenas.local/", result.WebUiUrl);
    }

    /// <summary>Verifies that a custom middleware port is retained by the derived TrueNAS Web UI URL.</summary>
    [TestMethod]
    public async Task GetAsync_NonDefaultEndpointPort_PreservesPortInWebUiUrl()
    {
        var resolver = new FakeHostAddressResolver([IPAddress.Parse("10.0.0.21")]);
        var service = CreateService("wss://truenas.local:8443/api/current", resolver);

        var result = await service.GetAsync();

        Assert.AreEqual("http://truenas.local:8443/", result.WebUiUrl);
    }

    private static TrueNasServerAddressService CreateService(string endpoint, IHostAddressResolver resolver) =>
        new(TrueNasEndpointOptions.Parse(endpoint), resolver, NullLogger<TrueNasServerAddressService>.Instance);

    private sealed class FakeHostAddressResolver : IHostAddressResolver
    {
        private readonly IPAddress[] addresses;
        private readonly Exception? exception;

        public FakeHostAddressResolver(IPAddress[] addresses)
        {
            this.addresses = addresses;
        }

        public FakeHostAddressResolver(Exception exception)
        {
            addresses = [];
            this.exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string hostName, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return exception is null ? Task.FromResult(addresses) : Task.FromException<IPAddress[]>(exception);
        }
    }
}
