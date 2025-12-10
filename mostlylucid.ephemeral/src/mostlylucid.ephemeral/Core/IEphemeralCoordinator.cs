namespace Mostlylucid.Ephemeral;

/// <summary>
///     Common interface for all ephemeral coordinators.
///     Used by SignalSink to orchestrate drain operations across multiple coordinators.
/// </summary>
public interface IEphemeralCoordinator : IAsyncDisposable
{
    /// <summary>
    ///     Complete intake (no new items) and wait for all work to finish.
    /// </summary>
    Task DrainAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether Complete() has been called.
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    ///     Whether all work is done (completed + drained).
    /// </summary>
    bool IsDrained { get; }

    /// <summary>
    ///     Signal that no more items will be added.
    /// </summary>
    void Complete();
}
