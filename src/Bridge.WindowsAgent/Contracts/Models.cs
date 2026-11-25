using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge.WindowsAgent.Contracts;

/// <summary>
/// Untrusted wire DTO. Deserialization is NOT authentication, authorization or validation.
/// Principal/controller identity is obtained from an authenticated transport, never this body.
/// Exact payload schemas and key allowlists are owned by the package's protocol contract.
/// Observe is a separate read operation; mutation envelopes admit only the three operations below.
/// </summary>
public sealed record ActionEnvelope
{
    [JsonPropertyName("schema_version")] public required string SchemaVersion { get; init; }
    [JsonPropertyName("command_id")] public required Guid CommandId { get; init; }
    [JsonPropertyName("device_id")] public required string DeviceId { get; init; }
    [JsonPropertyName("lease_id")] public required Guid LeaseId { get; init; }
    [JsonPropertyName("epoch")] public required long Epoch { get; init; }
    [JsonPropertyName("sequence")] public required long Sequence { get; init; }
    [JsonPropertyName("operation")] public required string Operation { get; init; }
    [JsonPropertyName("observation_id")] public required Guid ObservationId { get; init; }
    [JsonPropertyName("issued_at")] public required DateTimeOffset IssuedAt { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("payload")] public required JsonElement Payload { get; init; }
}

public static class OperationNames
{
    public const string Click = "input.click";
    public const string TypeText = "input.type_text";
    public const string Key = "input.key";
    public const string Observe = "observe";
    public const string FileList = "file.list";
    public const string FileRead = "file.read";
    public const string FileWrite = "file.write";
    public const string FileMove = "file.move";
    public const string FileMkdir = "file.mkdir";
}

/// <summary>v1.0 performs one left click; no button, duration or double-click field is admitted.</summary>
public sealed record ClickPayload(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

public sealed record TypeTextPayload(
    [property: JsonPropertyName("text")] string Text);

public sealed record KeyPayload(
    [property: JsonPropertyName("keys")] IReadOnlyList<string> Keys);

/// <summary>
/// File API payloads (T22). `path` is relative to the authorized
/// root, or absolute inside it; the backend rejects anything else.
/// `content_b64` is base64; `expected_hash` is lowercase SHA-256 of
/// the CURRENT content; `create_only` refuses to overwrite.
/// </summary>
public sealed record FileListPayload(
    [property: JsonPropertyName("path")] string Path);

public sealed record FileReadPayload(
    [property: JsonPropertyName("path")] string Path);

public sealed record FileWritePayload(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content_b64")] string ContentB64,
    [property: JsonPropertyName("create_only")] bool CreateOnly = false,
    [property: JsonPropertyName("expected_hash")] string? ExpectedHash = null);

/// <summary>Move/rename within the authorized root. Destination must not exist.</summary>
public sealed record FileMovePayload(
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("dest_path")] string DestPath);

/// <summary>Create a directory within the authorized root (parents included, idempotent).</summary>
public sealed record FileMkdirPayload(
    [property: JsonPropertyName("path")] string Path);

/// <summary>Origin may be negative. Width/height must be positive at the validation boundary.</summary>
public sealed record NativeRectangle(
    [property: JsonPropertyName("left")] int Left,
    [property: JsonPropertyName("top")] int Top,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record ObservationRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId);

/// <summary>Process-internal request. Epoch comes from the authenticated lease, never the HTTP body.</summary>
public sealed record InternalObservationRequest(ObservationRequest Request, long Epoch);

/// <summary>
/// Authenticated image metadata; artifact_id is an opaque UUID, never a download URL or local path.
/// A future validation boundary must require lowercase SHA256, image/png and 1..4194304 bytes.
/// This record alone does not enforce those conditions.
/// </summary>
public sealed record ObservationArtifact
{
    [JsonPropertyName("artifact_id")] public required Guid ArtifactId { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("mime_type")] public required string MimeType { get; init; }
    [JsonPropertyName("byte_length")] public required long ByteLength { get; init; }
}

/// <summary>
/// Local immutable capture metadata. Bytes use a separate bounded image channel.
/// image pixels map through crop_native into physical virtual-desktop coordinates.
/// Expiry alone is insufficient: lease, epoch, layout, foreground and input generations must match.
/// SchemaVersion must be 1.0; full field/range validation is not implemented by this DTO.
/// </summary>
public sealed record Observation
{
    [JsonPropertyName("schema_version")] public required string SchemaVersion { get; init; }
    [JsonPropertyName("observation_id")] public required Guid ObservationId { get; init; }
    [JsonPropertyName("device_id")] public required string DeviceId { get; init; }
    [JsonPropertyName("lease_id")] public required Guid LeaseId { get; init; }
    [JsonPropertyName("epoch")] public required long Epoch { get; init; }
    [JsonPropertyName("layout_revision")] public required long LayoutRevision { get; init; }
    [JsonPropertyName("foreground_generation")] public required long ForegroundGeneration { get; init; }
    [JsonPropertyName("input_generation")] public required long InputGeneration { get; init; }
    [JsonPropertyName("foreground_identity")] public required string ForegroundIdentity { get; init; }
    [JsonPropertyName("captured_at")] public required DateTimeOffset CapturedAt { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("artifact")] public required ObservationArtifact Artifact { get; init; }
    [JsonPropertyName("image_width")] public required int ImageWidth { get; init; }
    [JsonPropertyName("image_height")] public required int ImageHeight { get; init; }
    [JsonPropertyName("crop_native")] public required NativeRectangle CropNative { get; init; }
    [JsonPropertyName("virtual_desktop")] public required NativeRectangle VirtualDesktop { get; init; }
    [JsonPropertyName("uia"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Uia { get; init; }
}

/// <summary>Capabilities are measured on the current desktop, not inferred from the OS label.</summary>
public sealed record DesktopCapabilities
{
    [JsonPropertyName("backend_state")] public required string BackendState { get; init; }
    [JsonPropertyName("interactive_session")] public required bool InteractiveSession { get; init; }
    [JsonPropertyName("desktop_unlocked")] public required bool DesktopUnlocked { get; init; }
    [JsonPropertyName("observe")] public required bool Observe { get; init; }
    [JsonPropertyName("input")] public required bool Input { get; init; }
    [JsonPropertyName("uia")] public required bool Uia { get; init; }
    [JsonPropertyName("multi_monitor")] public required bool MultiMonitor { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

/// <summary>
/// Internal authenticated context. Do not deserialize it from a model's request or log its secrets.
/// It deliberately carries identity rather than a bearer token.
/// </summary>
public sealed record AuthenticatedController(string PrincipalId, string ControllerId);

/// <summary>
/// Internal backend completion receipt, not the HTTP 202 acceptance/status response schema.
/// Pre-acceptance rejection is an HTTP error and must not create a rejected command state.
/// </summary>
public sealed record ActionResult(
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("executed_steps")] int ExecutedSteps);

/// <summary>Timeout after accepted input has unknown effects: use uncertain and never replay.</summary>
public static class CommandStates
{
    public const string Accepted = "accepted";
    public const string Executing = "executing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";
    public const string Cancelled = "cancelled";
}
