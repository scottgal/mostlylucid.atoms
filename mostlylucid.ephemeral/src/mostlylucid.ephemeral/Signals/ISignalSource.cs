namespace Mostlylucid.Ephemeral;

/// <summary>
///     Represents an entity that can emit, store, and manage signals.
///     Both operations and coordinators implement this interface to provide
///     consistent signal management behavior.
/// </summary>
/// <remarks>
///     This interface extends ISignalEmitter to add signal storage and cleanup capabilities.
///     Operations store signals about their own execution (e.g., "api.request", "atom.start").
///     Coordinators store signals about coordination state (e.g., "coordinator.paused", "coordinator.drained").
/// </remarks>
public interface ISignalSource : ISignalEmitter
{
    /// <summary>
    ///     Gets all signals from this source.
    /// </summary>
    /// <returns>Read-only list of signal events.</returns>
    IReadOnlyList<SignalEvent> GetSignals();

    /// <summary>
    ///     Cleans up signals older than the specified age.
    /// </summary>
    /// <param name="olderThan">Remove signals older than this duration.</param>
    /// <returns>Number of signals removed.</returns>
    int CleanupSignals(TimeSpan olderThan);

    /// <summary>
    ///     Cleans up signals, keeping only the most recent N signals.
    /// </summary>
    /// <param name="keepCount">Number of most recent signals to keep.</param>
    /// <returns>Number of signals removed.</returns>
    int CleanupSignals(int keepCount);

    /// <summary>
    ///     Cleans up signals matching the specified pattern.
    /// </summary>
    /// <param name="pattern">Glob-style pattern to match signal names (supports * and ?).</param>
    /// <returns>Number of signals removed.</returns>
    int CleanupSignals(string pattern);
}
