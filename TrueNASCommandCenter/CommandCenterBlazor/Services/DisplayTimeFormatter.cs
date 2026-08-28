namespace TrueNasCommandCenter.Services;

/// <summary>Formats persisted UTC timestamps consistently for operator-facing pages.</summary>
public static class DisplayTimeFormatter
{
    /// <summary>Formats a UTC timestamp in the selected timezone using a 12-hour clock.</summary>
    /// <param name="utc">The persisted UTC timestamp.</param>
    /// <param name="timeZoneId">The optional IANA timezone identifier.</param>
    /// <param name="fallback">The text returned when the timestamp is absent.</param>
    /// <returns>The localized timestamp or the provided fallback.</returns>
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
                return $"{TimeZoneInfo.ConvertTimeFromUtc(value, zone):MMM d, yyyy h:mm tt} {zone.Id}";
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return $"{value:MMM d, yyyy h:mm tt} UTC";
    }
}
