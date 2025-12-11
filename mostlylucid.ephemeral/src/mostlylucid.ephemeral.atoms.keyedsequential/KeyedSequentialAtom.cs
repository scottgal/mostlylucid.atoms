namespace Mostlylucid.Ephemeral.Atoms.KeyedSequential;

/// <summary>
///     Per-key sequential atom. Ensures ordering per key while allowing global parallelism.
/// </summary>
public sealed class KeyedSequentialAtom<T, TKey> : AtomBase<EphemeralKeyedWorkCoordinator<T, TKey>>
    where TKey : notnull
{
    private long _id;

    public KeyedSequentialAtom(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        int? maxConcurrency = null,
        int perKeyConcurrency = 1,
        bool enableFairScheduling = false,
        SignalSink? signals = null)
        : base(new EphemeralKeyedWorkCoordinator<T, TKey>(keySelector, body, new EphemeralOptions
        {
            MaxConcurrency = ConcurrencyHelper.ResolveDefaultConcurrency(maxConcurrency),
            MaxConcurrencyPerKey = Math.Max(1, perKeyConcurrency),
            EnableFairScheduling = enableFairScheduling,
            Signals = signals
        }))
    {
    }

    public async ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        await Coordinator.EnqueueAsync(item, ct).ConfigureAwait(false);
        return Interlocked.Increment(ref _id);
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