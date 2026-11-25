namespace Bridge.DesktopCore;

/// <summary>
/// Input validation shared by the worker and the tray (docs/04 §6,
/// contract_lab ALLOWED_KEYS + payload rules). Pure: no I/O, no
/// Windows calls, so it is unit-tested on Linux.
/// </summary>
public static class InputValidation
{
    /// <summary>Exact copy of contract_lab ALLOWED_KEYS.</summary>
    public static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "CTRL", "ALT", "SHIFT", "WIN", "ENTER", "TAB", "ESC",
        "BACKSPACE", "DELETE", "SPACE", "HOME", "END", "LEFT",
        "RIGHT", "UP", "DOWN", "PAGEUP", "PAGEDOWN",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
    };

    public const int MaxTypeTextLength = 2000;
    public const int MaxKeyCount = 4;
    public const int MaxCommandTtlSeconds = 30;
    public const int MaxClockSkewSeconds = 2;

    public sealed record ValidationError(string Code, string Message);

    public static ValidationError? ValidateClick(int x, int y)
    {
        if (x < 0 || y < 0)
            return new ValidationError("invalid_coordinates", "Click coordinates must be non-negative integers.");
        return null;
    }

    public static ValidationError? ValidateTypeText(string? text)
    {
        if (text is null)
            return new ValidationError("invalid_payload", "text is required.");
        if (text.Length is < 1 or > MaxTypeTextLength)
            return new ValidationError("invalid_payload", $"text must be 1..{MaxTypeTextLength} characters.");
        if (text.Contains('\0'))
            return new ValidationError("invalid_payload", "text must not contain NUL.");
        return null;
    }

    public static ValidationError? ValidateKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null)
            return new ValidationError("invalid_payload", "keys is required.");
        if (keys.Count is < 1 or > MaxKeyCount)
            return new ValidationError("invalid_payload", $"keys must have 1..{MaxKeyCount} entries.");
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            return new ValidationError("invalid_payload", "keys must not repeat.");
        foreach (var key in keys)
        {
            if (!AllowedKeys.Contains(key))
                return new ValidationError("invalid_payload", $"Key '{key}' is not in the allowlist.");
        }
        return null;
    }

    /// <summary>Envelope time rules (docs/04 §6).</summary>
    public static ValidationError? ValidateTimes(
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (issuedAt > now.AddSeconds(MaxClockSkewSeconds))
            return new ValidationError("clock_skew", "issued_at is too far in the future.");
        var ttl = expiresAt - issuedAt;
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromSeconds(MaxCommandTtlSeconds))
            return new ValidationError("bad_ttl", "Command TTL must be in (0, 30s].");
        if (expiresAt <= now)
            return new ValidationError("command_expired", "Command already expired.");
        return null;
    }

    /// <summary>PNG artifact metadata (docs/04 §8–§9).</summary>
    public static ValidationError? ValidateArtifact(
        string sha256, string mimeType, long byteLength)
    {
        if (mimeType != "image/png")
            return new ValidationError("invalid_artifact", "mime_type must be image/png.");
        if (byteLength is < 1 or > 4 * 1024 * 1024)
            return new ValidationError("invalid_artifact", "byte_length must be 1..4194304.");
        if (sha256.Length != 64 || sha256.Any(c => !"0123456789abcdef".Contains(c)))
            return new ValidationError("invalid_artifact", "sha256 must be 64 lowercase hex characters.");
        return null;
    }
}
