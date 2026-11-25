namespace Bridge.Relay;

/// <summary>
/// Contract failure, mirroring contract_lab's ContractError codes.
/// Codes are the wire contract (docs/04 §7); never leak stack traces
/// or internal detail in the code/message pair.
/// </summary>
public sealed class ContractError : Exception
{
    public string Code { get; }
    public ContractError(string code, string message) : base(message) => Code = code;
}
