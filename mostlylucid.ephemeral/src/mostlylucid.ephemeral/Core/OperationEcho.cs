namespace Mostlylucid.Ephemeral;

/// <summary>
///     Short-lived echo of a finalized operation for observability/auditing.
/// </summary>
public sealed record OperationEcho(
    long OperationId,
    string? Key,
    IReadOnlyList<SignalEvent>? Signals,
    DateTimeOffset FinalizedAt);