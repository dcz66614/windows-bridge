using System.Globalization;
using System.Text.Json;

namespace Bridge.Contracts;

/// <summary>
/// Strict envelope validation, ported from starter/contract_lab
/// (bridge_contract.py) and starter/muse_client (client.py).
/// Rules (docs/04 §6, schemas/command.schema.json):
///  - exactly the 11 envelope fields, no unknown fields, no duplicate keys
///  - schema_version == "1.0"; ids are lowercase standard UUIDs
///  - device_id 1..128 ASCII [A-Za-z0-9_.:-]; epoch/sequence 1..Int64.Max
///  - times RFC3339 UTC 'Z', 0..6 fractional digits; TTL (0,30s];
///    issued_at at most 2s ahead of the validating clock
///  - operations input.click / input.type_text / input.key only, with
///    per-operation payload shapes; no button/duration/script smuggling.
/// Validation failures raise <see cref="EnvelopeException"/>; a validated
/// envelope is NOT an authorization decision — lease/epoch/ownership are
/// checked by the relay/agent against trusted context.
/// </summary>
public static class EnvelopeValidator
{
    private static readonly string[] EnvelopeFields =
    {
        "schema_version", "command_id", "device_id", "lease_id", "epoch",
        "sequence", "operation", "observation_id", "issued_at", "expires_at",
        "payload",
    };

    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(
        new[]
        {
            "CTRL", "ALT", "SHIFT", "WIN", "ENTER", "TAB", "ESC", "BACKSPACE",
            "DELETE", "SPACE", "HOME", "END", "LEFT", "RIGHT", "UP", "DOWN",
            "PAGEUP", "PAGEDOWN",
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10",
            "F11", "F12",
        });

    public static CommandEnvelope Validate(string json, DateTimeOffset nowUtc)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new EnvelopeException("BAD_JSON", "Request is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new EnvelopeException("BAD_SCHEMA", "Envelope must be a JSON object.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                if (!seen.Add(prop.Name))
                    throw new EnvelopeException("DUPLICATE_KEY", $"Duplicate JSON key: {prop.Name}.");
                if (!EnvelopeFields.Contains(prop.Name, StringComparer.Ordinal))
                    throw new EnvelopeException("UNKNOWN_FIELD", $"Unknown envelope field: {prop.Name}.");
            }
            foreach (var field in EnvelopeFields)
            {
                if (!seen.Contains(field))
                    throw new EnvelopeException("MISSING_FIELD", $"Missing envelope field: {field}.");
            }

            string schemaVersion = RequiredString(root, "schema_version");
            if (schemaVersion != "1.0")
                throw new EnvelopeException("BAD_SCHEMA", "schema_version must be \"1.0\".");

            string commandId = RequiredUuid(root, "command_id");
            string leaseId = RequiredUuid(root, "lease_id");
            string observationId = RequiredUuid(root, "observation_id");
            string deviceId = RequiredDeviceId(root);
            long epoch = RequiredPositiveInt64(root, "epoch");
            long sequence = RequiredPositiveInt64(root, "sequence");
            string operation = RequiredString(root, "operation");
            DateTimeOffset issuedAt = RequiredUtcTime(root, "issued_at");
            DateTimeOffset expiresAt = RequiredUtcTime(root, "expires_at");

            var ttl = expiresAt - issuedAt;
            if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromSeconds(30))
                throw new EnvelopeException("BAD_TTL", "expires_at - issued_at must be in (0, 30s].");
            if (issuedAt > nowUtc.AddSeconds(2))
                throw new EnvelopeException("CLOCK_SKEW", "issued_at is more than 2s ahead of the validating clock.");
            if (expiresAt <= nowUtc)
                throw new EnvelopeException("EXPIRED", "Command envelope has already expired.");

            CommandPayload payload = operation switch
            {
                "input.click" => ParseClick(RequiredObject(root, "payload")),
                "input.type_text" => ParseTypeText(RequiredObject(root, "payload")),
                "input.key" => ParseKey(RequiredObject(root, "payload")),
                _ => throw new EnvelopeException("UNKNOWN_OPERATION", $"Unsupported operation: {operation}."),
            };

            return new CommandEnvelope(commandId, deviceId, leaseId, epoch, sequence,
                operation, observationId, issuedAt, expiresAt, payload);
        }
    }

    private static ClickPayload ParseClick(JsonElement payload)
    {
        ExpectExactFields(payload, "x", "y");
        int x = RequiredInt32(payload, "x");
        int y = RequiredInt32(payload, "y");
        if (x < 0 || y < 0)
            throw new EnvelopeException("BAD_COORDINATE", "Click coordinates must be non-negative.");
        return new ClickPayload(x, y);
    }

    private static TypeTextPayload ParseTypeText(JsonElement payload)
    {
        ExpectExactFields(payload, "text");
        string text = RequiredString(payload, "text");
        int codePoints = CountCodePoints(text);
        if (codePoints < 1 || codePoints > 2000)
            throw new EnvelopeException("BAD_TEXT", "text must be 1..2000 Unicode code points.");
        if (text.Contains('\0'))
            throw new EnvelopeException("BAD_TEXT", "text must not contain NUL.");
        return new TypeTextPayload(text);
    }

    private static KeyPayload ParseKey(JsonElement payload)
    {
        ExpectExactFields(payload, "keys");
        var keysEl = payload.GetProperty("keys");
        if (keysEl.ValueKind != JsonValueKind.Array)
            throw new EnvelopeException("BAD_SCHEMA", "keys must be an array.");
        var keys = new List<string>();
        foreach (var k in keysEl.EnumerateArray())
        {
            if (k.ValueKind != JsonValueKind.String)
                throw new EnvelopeException("BAD_SCHEMA", "Each key must be a string.");
            string key = k.GetString()!;
            if (!AllowedKeys.Contains(key))
                throw new EnvelopeException("BAD_KEY", $"Key not in allowlist: {key}.");
            if (!keys.Contains(key, StringComparer.Ordinal))
                keys.Add(key);
            else
                throw new EnvelopeException("BAD_KEY", $"Duplicate key in combination: {key}.");
        }
        if (keys.Count < 1 || keys.Count > 4)
            throw new EnvelopeException("BAD_KEY", "keys must contain 1..4 distinct keys.");
        return new KeyPayload(keys);
    }

    private static void ExpectExactFields(JsonElement obj, params string[] fields)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw new EnvelopeException("BAD_SCHEMA", "payload must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
                throw new EnvelopeException("DUPLICATE_KEY", $"Duplicate JSON key: {prop.Name}.");
            if (!fields.Contains(prop.Name, StringComparer.Ordinal))
                throw new EnvelopeException("UNKNOWN_FIELD", $"Unknown payload field: {prop.Name}.");
        }
        foreach (var f in fields)
        {
            if (!seen.Contains(f))
                throw new EnvelopeException("MISSING_FIELD", $"Missing payload field: {f}.");
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        var el = parent.GetProperty(name);
        if (el.ValueKind != JsonValueKind.Object)
            throw new EnvelopeException("BAD_SCHEMA", $"{name} must be an object.");
        return el;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var el = parent.GetProperty(name);
        if (el.ValueKind != JsonValueKind.String)
            throw new EnvelopeException("BAD_SCHEMA", $"{name} must be a string.");
        return el.GetString()!;
    }

    private static string RequiredUuid(JsonElement parent, string name)
    {
        string value = RequiredString(parent, name);
        if (!Guid.TryParseExact(value, "D", out _) || value != value.ToLowerInvariant())
            throw new EnvelopeException("BAD_UUID", $"{name} must be a lowercase standard UUID.");
        return value;
    }

    private static string RequiredDeviceId(JsonElement parent)
    {
        string value = RequiredString(parent, "device_id");
        if (value.Length < 1 || value.Length > 128)
            throw new EnvelopeException("BAD_DEVICE_ID", "device_id must be 1..128 characters.");
        foreach (char c in value)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c is '_' or '.' or ':' or '-';
            if (!ok)
                throw new EnvelopeException("BAD_DEVICE_ID", "device_id has a disallowed character.");
        }
        return value;
    }

    private static long RequiredPositiveInt64(JsonElement parent, string name)
    {
        var el = parent.GetProperty(name);
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out long value))
            throw new EnvelopeException("BAD_SCHEMA", $"{name} must be an integer.");
        if (value < 1)
            throw new EnvelopeException("BAD_SCHEMA", $"{name} must be a positive integer.");
        return value;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        var el = parent.GetProperty(name);
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int value))
            throw new EnvelopeException("BAD_SCHEMA", $"{name} must be a 32-bit integer.");
        return value;
    }

    private static DateTimeOffset RequiredUtcTime(JsonElement parent, string name)
    {
        string value = RequiredString(parent, name);
        // RFC3339 UTC 'Z', at most 6 fractional digits; no offset forms.
        if (!value.EndsWith("Z", StringComparison.Ordinal) || value.Length < 20)
            throw new EnvelopeException("BAD_TIME", $"{name} must be RFC3339 UTC ending in 'Z'.");
        string frac = value.Contains('.') ? value[(value.IndexOf('.') + 1)..^1] : string.Empty;
        if (frac.Length > 6 || frac.Any(c => c < '0' || c > '9'))
            throw new EnvelopeException("BAD_TIME", $"{name} allows at most 6 fractional digits.");
        if (!DateTimeOffset.TryParseExact(value,
                new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new EnvelopeException("BAD_TIME", $"{name} is not a valid UTC timestamp.");
        }
        return parsed;
    }

    internal static int CountCodePoints(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }
                else
                {
                    throw new EnvelopeException("BAD_TEXT", "text contains a lone surrogate.");
                }
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                throw new EnvelopeException("BAD_TEXT", "text contains a lone surrogate.");
            }
            count++;
        }
        return count;
    }
}

public sealed class EnvelopeException : Exception
{
    public string Code { get; }
    public EnvelopeException(string code, string message) : base(message) => Code = code;
    public EnvelopeException(string code, string message, Exception inner) : base(message, inner) => Code = code;
}
