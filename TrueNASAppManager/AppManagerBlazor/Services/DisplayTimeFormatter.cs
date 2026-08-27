namespace TrueNasAppManager.Services;

public static class DisplayTimeFormatter
{
    public static string Format(DateTime? utc, string? timeZoneId, string fallback = "Never")
    {
        if (utc is null)
        {
            return fallback;
        }

        var value = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return $"{TimeZoneInfo.ConvertTimeFromUtc(value, zone):MMM d, yyyy HH:mm} {zone.Id}";
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return $"{value:MMM d, yyyy HH:mm} UTC";
    }
}
