using System.Text.RegularExpressions;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class ApplicationVersionTests
{
    [TestMethod]
    public void Current_IsEmbeddedSemanticVersion()
    {
        Assert.MatchesRegex(new Regex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant), ApplicationVersion.Current);
        Assert.AreEqual($"v{ApplicationVersion.Current}", ApplicationVersion.Display);
    }
}
