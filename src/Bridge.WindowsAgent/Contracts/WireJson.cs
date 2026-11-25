using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bridge.WindowsAgent.Contracts;

/// <summary>Serialization helper only. Full schema/range/identity validation is still unimplemented.</summary>
public static class WireJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16
        };
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }
}

internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    private static readonly Regex Format = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Timestamp must be a UTC RFC3339 string ending in Z.");
        var value = reader.GetString();
        if (value is null || value.Length > 27 || !Format.IsMatch(value) ||
            !DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
            throw new JsonException("Invalid UTC RFC3339 timestamp.");
        return timestamp;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
}
