namespace Mostlylucid.Ephemeral;

/// <summary>
///     Minimal state captured when an operation is finalized.
/// </summary>
public sealed record LastWordsNote(
    long OperationId,
    string? Key,
    string? Signal,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string?>? Metadata = null);