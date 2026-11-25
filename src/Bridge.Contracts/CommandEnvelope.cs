namespace Bridge.Contracts;

/// <summary>Validated command envelope (protocol spec 1.0, docs/04 §6).
/// Instances are only produced by <see cref="EnvelopeValidator"/>; never
/// trust a caller-supplied owner/controller — those come from the
/// authenticated transport context, not from the JSON body.</summary>
public sealed record CommandEnvelope(
    string CommandId,
    string DeviceId,
    string LeaseId,
    long Epoch,
    long Sequence,
    string Operation,
    string ObservationId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    CommandPayload Payload);

public abstract record CommandPayload;
public sealed record ClickPayload(int X, int Y) : CommandPayload;
public sealed record TypeTextPayload(string Text) : CommandPayload;
public sealed record KeyPayload(IReadOnlyList<string> Keys) : CommandPayload;
