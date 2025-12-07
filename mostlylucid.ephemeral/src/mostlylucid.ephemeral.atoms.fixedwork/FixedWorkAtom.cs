using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.FixedWork;

/// <summary>
/// Minimal "atom" wrapper around EphemeralWorkCoordinator for fixed-concurrency pipelines.
/// Keeps API small: enqueue, complete, drain, snapshot.
/// </summary>
public sealed class FixedWorkAtom<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;

    public FixedWorkAtom(
        Func<T, CancellationToken, Task> body,
        int? maxConcurrency = null,
        int? maxTracked = null,
        SignalSink? signals = null)
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
            MaxTrackedOperations = maxTracked is > 0 ? maxTracked.Value : 200,
            Signals = signals
        };

        _coordinator = new EphemeralWorkCoordinator<T>(body, options);
    }

    /// <summary>
    /// Enqueue and get the operation id.
    /// </summary>
    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
        => _coordinator.EnqueueWithIdAsync(item, ct);

    /// <summary>
    /// Prevent new work and drain outstanding operations.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshot recent operations.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot() => _coordinator.GetSnapshot();

    /// <summary>
    /// Aggregate health stats for dashboards.
    /// </summary>
    public (int Pending, int Active, int Completed, int Failed) Stats()
        => (_coordinator.PendingCount, _coordinator.ActiveCount, _coordinator.TotalCompleted, _coordinator.TotalFailed);

    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();
}
