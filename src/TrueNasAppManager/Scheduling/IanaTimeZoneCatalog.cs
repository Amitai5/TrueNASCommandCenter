namespace TrueNasAppManager.Scheduling;

public static class IanaTimeZoneCatalog
{
    /// <summary>Gets the sorted IANA timezone identifiers available on the current platform.</summary>
    public static IReadOnlyList<string> Ids { get; } = buildIds();

    private static IReadOnlyList<string> buildIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out _))
            {
                ids.Add(zone.Id);
            }
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId))
            {
                ids.Add(ianaId);
            }
        }

        ids.Add("Etc/UTC");
        return ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
}
