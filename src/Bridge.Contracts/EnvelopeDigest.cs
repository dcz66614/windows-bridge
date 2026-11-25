using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bridge.Contracts;

/// <summary>
/// Canonical envelope digest (docs/04 §7, T08 golden vectors).
/// Rule (contract_lab bridge_contract.py:297):
///   json.dumps(command, sort_keys=True, ensure_ascii=False,
///              separators=(",", ":"), allow_nan=False).encode("utf-8")
///   -> SHA-256 hex.
/// The writer below is hand-rolled so escaping matches Python exactly:
/// object keys sorted ordinally, no spaces, raw UTF-8 for non-ASCII,
/// string escapes \" \\ \b \f \n \r \t and \u00xx (lowercase hex) for
/// other control characters. Numbers are emitted as their validated
/// raw text (envelopes carry integers only after validation).
/// Source of truth: tests/golden-vectors.json (generated + verified
/// against contract_lab's own _validate_envelope).
/// </summary>
public static class EnvelopeDigest
{
    public static string CanonicalJson(JsonElement element)
    {
        var sb = new StringBuilder();
        WriteCanonical(element, sb);
        return sb.ToString();
    }

    public static string Sha256Hex(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();

    public static string DigestEnvelope(string validatedEnvelopeJson)
    {
        using var doc = JsonDocument.Parse(validatedEnvelopeJson);
        return Sha256Hex(CanonicalJson(doc.RootElement));
    }

    private static void WriteCanonical(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (var prop in el.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteCanonicalString(prop.Name, sb);
                    sb.Append(':');
                    WriteCanonical(prop.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                first = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteCanonical(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteCanonicalString(el.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                sb.Append(el.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON kind: {el.ValueKind}");
        }
    }

    private static void WriteCanonicalString(string value, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
