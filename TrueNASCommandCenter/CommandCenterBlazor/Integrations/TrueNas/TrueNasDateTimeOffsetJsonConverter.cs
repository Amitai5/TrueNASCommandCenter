using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueNasCommandCenter.Integrations.TrueNas;

/// <summary>Reads TrueNAS timestamps from ISO strings, Unix values, and extended-JSON date wrappers.</summary>
public sealed class TrueNasDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private static readonly HashSet<string> DatePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$date",
        "$numberLong",
        "date",
        "datetime",
        "timestamp",
        "value",
        "rawvalue",
        "parsed"
    };

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (TryReadDate(document.RootElement, out var value))
        {
            return value;
        }

        throw new JsonException("TrueNAS returned an unsupported timestamp value.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());

    private static bool TryReadDate(JsonElement element, out DateTimeOffset value)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            {
                return true;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixValue))
            {
                return TryReadUnixTime(unixValue, out value);
            }
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numericValue))
        {
            return TryReadUnixTime(numericValue, out value);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (DatePropertyNames.Contains(property.Name) && TryReadDate(property.Value, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadUnixTime(long value, out DateTimeOffset result)
    {
        try
        {
            result = value > 9_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }
}
