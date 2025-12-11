namespace Mostlylucid.Ephemeral.Atoms.FixedWork;

/// <summary>
///     Minimal "atom" wrapper around EphemeralWorkCoordinator for fixed-concurrency pipelines.
///     Keeps API small: enqueue, complete, drain, snapshot.
/// </summary>
public sealed class FixedWorkAtom<T> : AtomBase<EphemeralWorkCoordinator<T>>
{
    public FixedWorkAtom(
        Func<T, CancellationToken, Task> body,
        int? maxConcurrency = null,
        int? maxTracked = null,
        SignalSink? signals = null,
        int maxSignalCount = 0,
        TimeSpan? maxSignalAge = null)
        : base(
            new EphemeralWorkCoordinator<T>(body, new EphemeralOptions
            {
                MaxConcurrency = ConcurrencyHelper.ResolveDefaultConcurrency(maxConcurrency),
                MaxTrackedOperations = maxTracked is > 0 ? maxTracked.Value : 200,
                Signals = signals
            }),
            maxSignalCount,
            maxSignalAge)
    {
    }

    /// <summary>
    ///     Enqueue and get the operation id.
    /// </summary>
    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        return Coordinator.EnqueueWithIdAsync(item, ct);
    }

    protected override void Complete()
    {
        Coordinator.Complete();
    }

    protected override Task DrainInternalAsync(CancellationToken ct)
    {
        return Coordinator.DrainAsync(ct);
    }

    public override IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot()
    {
        return Coordinator.GetSnapshot();
    }
}