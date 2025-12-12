namespace Mostlylucid.Ephemeral;

/// <summary>
///     Non-generic base interface for all ephemeral coordinators.
///     Provides common properties and methods that don't depend on operation type.
/// </summary>
public interface ICoordinator : IAsyncDisposable, ISignalSource
{
    /// <summary>
    ///     Number of items waiting to be processed.
    /// </summary>
    int PendingCount { get; }

    /// <summary>
    ///     Number of items currently being processed.
    /// </summary>
    int ActiveCount { get; }

    /// <summary>
    ///     Total number of items enqueued since creation.
    /// </summary>
    int TotalEnqueued { get; }

    /// <summary>
    ///     Total number of successfully completed items.
    /// </summary>
    int TotalCompleted { get; }

    /// <summary>
    ///     Total number of failed items.
    /// </summary>
    int TotalFailed { get; }

    /// <summary>
    ///     Whether Complete() has been called.
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    ///     Whether all work is done (completed + drained).
    /// </summary>
    bool IsDrained { get; }

    /// <summary>
    ///     Whether the coordinator is paused.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    ///     Configuration options for this coordinator.
    /// </summary>
    EphemeralOptions Options { get; }

    /// <summary>
    ///     Gets a snapshot of all operations in this coordinator's window.
    /// </summary>
    IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot();
}
