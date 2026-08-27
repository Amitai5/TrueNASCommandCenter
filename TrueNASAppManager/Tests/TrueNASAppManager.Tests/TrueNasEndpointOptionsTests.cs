using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class TrueNasEndpointOptionsTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void Parse_ValidSecureEndpoint_NormalizesPath()
    {
        // Arrange
        const string value = "wss://truenas.example.test:8443/api/current/";

        // Act
        var result = TrueNasEndpointOptions.Parse(value);

        // Assert
        Assert.AreEqual(new Uri("wss://truenas.example.test:8443/api/current"), result.ServerUri);
        Assert.AreEqual("wss://truenas.example.test:8443/api/current", result.ServerUrl);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [TestCategory("Unit")]
    public void Parse_MissingEndpoint_ThrowsArgumentException(string? value)
    {
        // Act
        void Act() => TrueNasEndpointOptions.Parse(value);

        // Assert
        var exception = Assert.Throws<ArgumentException>(Act);
        StringAssert.Contains(exception.Message, "TRUENAS_WEBSOCKET_URL is required");
    }

    [TestMethod]
    [DataRow("ws://truenas.example.test/api/current")]
    [DataRow("https://truenas.example.test/api/current")]
    [DataRow("wss://user:secret@truenas.example.test/api/current")]
    [DataRow("wss://truenas.example.test/api/current?token=secret")]
    [DataRow("wss://truenas.example.test/api/current#fragment")]
    [DataRow("wss://truenas.example.test/api/other")]
    [TestCategory("Unit")]
    public void Parse_UnsafeOrIncorrectEndpoint_ThrowsArgumentException(string value)
    {
        // Act
        void Act() => TrueNasEndpointOptions.Parse(value);

        // Assert
        var exception = Assert.Throws<ArgumentException>(Act);
        StringAssert.Contains(exception.Message, "absolute wss:// URL ending in /api/current");
    }
}
