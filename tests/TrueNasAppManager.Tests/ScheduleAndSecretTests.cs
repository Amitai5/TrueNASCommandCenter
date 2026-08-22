using Microsoft.Extensions.Configuration;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Scheduling;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Tests;

[TestClass]
public sealed class ScheduleAndSecretTests
{
    [TestMethod]
    public void Validate_RejectsSecondsField()
    {
        var service = new ScheduleService(new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        var result = service.Validate("0 0 4 * * *", "UTC");

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Error, "exactly 5 fields");
    }

    [TestMethod]
    public void Validate_ReturnsThreeFutureRunsAfterRestart()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 44, 0, TimeSpan.Zero);
        var service = new ScheduleService(new FixedTimeProvider(now));

        var result = service.Validate("0 4 * * 0", "America/New_York");

        Assert.IsTrue(result.IsValid, result.Error);
        Assert.HasCount(3, result.NextRuns);
        Assert.IsTrue(result.NextRuns.All(run => run > now));
        Assert.IsTrue(result.NextRuns.SequenceEqual(result.NextRuns.OrderBy(run => run)));
    }

    [TestMethod]
    public void Validate_DstOccurrencesAreValidLocalTimes()
    {
        var now = new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero);
        var service = new ScheduleService(new FixedTimeProvider(now));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var result = service.Validate("30 2 * * *", zone.Id);

        Assert.IsTrue(result.IsValid, result.Error);
        foreach (var occurrence in result.NextRuns)
        {
            var local = TimeZoneInfo.ConvertTime(occurrence, zone).DateTime;
            Assert.IsFalse(zone.IsInvalidTime(local));
        }
    }

    [TestMethod]
    public void SecretProtector_RoundTripsWithAuthenticatedRandomEncryption()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"secret-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var key = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray());
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_ENCRYPTION_KEY"] = key })
                .Build();
            var protector = new AesGcmSecretProtector(new DataPathOptions(directory), configuration);

            var first = protector.Protect("sensitive-value");
            var second = protector.Protect("sensitive-value");

            Assert.AreNotEqual(first, second);
            Assert.DoesNotContain("sensitive-value", first);
            Assert.AreEqual("sensitive-value", protector.Unprotect(first));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(
                () => protector.Unprotect(first[..^2] + "AA"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
