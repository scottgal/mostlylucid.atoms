namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
/// Condensed echo of an operation that includes the captured payloads and lifecycle metadata.
/// </summary>
public sealed record OperationEchoEntry<TPayload>(
    long OperationId,
    string? Key,
    bool WasPinned,
    IReadOnlyList<OperationEchoCapture<TPayload>> Captures,
    DateTimeOffset FinalizedAt);
