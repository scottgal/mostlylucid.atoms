namespace Mostlylucid.Ephemeral;

/// <summary>
/// Immutable snapshot of an ephemeral operation's state.
/// </summary>
public sealed record EphemeralOperationSnapshot(
    long Id,
    DateTimeOffset Started,
    DateTimeOffset? Completed,
    string? Key,
    bool IsFaulted,
    Exception? Error,
    TimeSpan? Duration,
    IReadOnlyList<string>? Signals = null,
    bool IsPinned = false)
{
    /// <summary>
    /// Check if this operation raised a specific signal.
    /// </summary>
    public bool HasSignal(string signal) => Signals?.Contains(signal) == true;
}

/// <summary>
/// Snapshot of an operation that captures a result of type TResult.
/// </summary>
public sealed record EphemeralOperationSnapshot<TResult>(
    long Id,
    DateTimeOffset Started,
    DateTimeOffset? Completed,
    string? Key,
    bool IsFaulted,
    Exception? Error,
    TimeSpan? Duration,
    TResult? Result,
    bool HasResult,
    IReadOnlyList<string>? Signals = null,
    bool IsPinned = false)
{
    /// <summary>
    /// Check if this operation raised a specific signal.
    /// </summary>
    public bool HasSignal(string signal) => Signals?.Contains(signal) == true;
}
