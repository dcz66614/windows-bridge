using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge.DeviceChannel;

/// <summary>
/// The device channel wire protocol (docs/04 §10). One JSON text
/// frame per message: { type, message_id, schema_version, body }.
/// Types: hello, challenge, heartbeat, heartbeat_ack, command,
/// command_result, lease_update, revoke, error. schema_version is 1.
/// </summary>
public sealed record ChannelEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message_id")] string? MessageId,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("body")] JsonElement Body)
{
    public const int CurrentSchemaVersion = 1;

    public static ChannelEnvelope Create(string type, object? body, string? messageId = null)
    {
        var bodyJson = body is null
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : JsonSerializer.SerializeToElement(body);
        return new ChannelEnvelope(type, messageId, CurrentSchemaVersion, bodyJson);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static ChannelEnvelope? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChannelEnvelope>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Application close codes (4000–4999 range). 4401/4403 are
/// non-retryable: the client must NOT reconnect on them.</summary>
public static class ChannelCloseCodes
{
    public const int Unauthorized = 4401;
    public const int Revoked = 4403;
    public const int HelloTimeout = 4408;
    public const int Evicted = 4409;
    public const int HeartbeatTimeout = 4410;
    public const int OversizeFrame = 1009;

    public static bool IsRetryable(int? code) => code is null or < 4400 or > 4403;
}

/// <summary>Frame reading with a hard size cap (T11: max frame 1 MiB).
/// Returns null when the socket closed cleanly.</summary>
public static class ChannelFraming
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static async Task<string?> ReadMessageAsync(
        WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Binary frames are not accepted.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxFrameBytes)
                throw new InvalidOperationException("Frame exceeds 1 MiB.");
            if (result.EndOfMessage)
                break;
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static async Task SendAsync(
        WebSocket socket, ChannelEnvelope envelope, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJson());
        if (bytes.Length > MaxFrameBytes)
            throw new InvalidOperationException("Frame exceeds 1 MiB.");
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
