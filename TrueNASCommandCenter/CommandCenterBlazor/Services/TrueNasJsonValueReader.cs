using System.Globalization;
using System.Text.Json;

namespace TrueNasCommandCenter.Services;

internal static class TrueNasJsonValueReader
{
    public static string? FindString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out var value))
            {
                var direct = ScalarString(value);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return direct;
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindString(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    public static DateTimeOffset? FindDate(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (TryParseDate(element, out var direct))
        {
            return direct;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out var value))
            {
                if (TryParseDate(value, out var parsed))
                {
                    return parsed;
                }

                if (value.ValueKind == JsonValueKind.Object)
                {
                    var wrapped = FindDate(value, "parsed", "value", "rawvalue", "datetime", "timestamp");
                    if (wrapped is not null)
                    {
                        return wrapped;
                    }
                }
            }
        }

        return null;
    }

    public static double? FindDouble(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    public static bool ContainsInteger(JsonElement element, int expected)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number == expected;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(item => ContainsInteger(item, expected));
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject().Any(property => ContainsInteger(property.Value, expected));
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ScalarString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => null
    };

    private static bool TryParseDate(JsonElement element, out DateTimeOffset value)
    {
        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
        {
            value = value.ToUniversalTime();
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var unixValue))
        {
            try
            {
                value = unixValue > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue) : DateTimeOffset.FromUnixTimeSeconds(unixValue);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        value = default;
        return false;
    }
}
