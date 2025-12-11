namespace Mostlylucid.Ephemeral;

/// <summary>
///     Immutable snapshot of an ephemeral operation's state.
/// </summary>
public sealed record EphemeralOperationSnapshot(
    long Id,
    DateTimeOffset Started,
    DateTimeOffset? Completed,
    string? Key,
    bool IsFaulted,
    Exception? Error,
    TimeSpan? Duration,
    IReadOnlyList<SignalEvent>? Signals = null,
    bool IsPinned = false)
{
    /// <summary>
    ///     Backward-compatible accessor for the operation identity.
    /// </summary>
    public long OperationId => Id;

    /// <summary>
    ///     Check if this operation raised a specific signal.
    /// </summary>
    public bool HasSignal(string signal)
    {
        if (Signals is null) return false;
        foreach (var evt in Signals)
            if (evt.Signal == signal)
                return true;
        return false;
    }
}

/// <summary>
///     Snapshot of an operation that captures a result of type TResult.
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
    IReadOnlyList<SignalEvent>? Signals = null,
    bool IsPinned = false)
{
    /// <summary>
    ///     Backward-compatible accessor for the operation identity.
    /// </summary>
    public long OperationId => Id;

    /// <summary>
    ///     Check if this operation raised a specific signal.
    /// </summary>
    public bool HasSignal(string signal)
    {
        if (Signals is null) return false;
        foreach (var evt in Signals)
            if (evt.Signal == signal)
                return true;
        return false;
    }
}