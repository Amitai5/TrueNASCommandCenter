using System.Globalization;
using System.Text.RegularExpressions;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

public interface IVersionClassifier
{
    bool TryParse(string? value, out VersionParts? version);
    bool IsAllowed(string? installed, string? target, VersionScope scope);
}

public sealed partial class VersionClassifier : IVersionClassifier
{
    [GeneratedRegex(
        @"^[vV]?([0-9]+)\.([0-9]+)\.([0-9]+)(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public bool TryParse(string? value, out VersionParts? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = VersionPattern().Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new VersionParts(major, minor, patch);
        return true;
    }

    public bool IsAllowed(string? installed, string? target, VersionScope scope)
    {
        if (scope == VersionScope.AnyVersion)
        {
            return !string.IsNullOrWhiteSpace(target);
        }

        if (!TryParse(installed, out var current) || !TryParse(target, out var available))
        {
            return false;
        }

        return scope switch
        {
            VersionScope.MinorAndPatch => current!.Major == available!.Major,
            VersionScope.PatchOnly =>
                current!.Major == available!.Major &&
                current.Minor == available.Minor,
            _ => false
        };
    }
}
