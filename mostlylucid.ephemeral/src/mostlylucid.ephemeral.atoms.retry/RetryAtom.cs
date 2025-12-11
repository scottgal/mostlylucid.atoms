namespace Mostlylucid.Ephemeral.Atoms.Retry;

/// <summary>
///     Wraps work with retry/backoff semantics using EphemeralWorkCoordinator under the hood.
/// </summary>
public sealed class RetryAtom<T> : AtomBase<EphemeralWorkCoordinator<T>>
{
    public RetryAtom(
        Func<T, CancellationToken, Task> body,
        int maxAttempts = 3,
        Func<int, TimeSpan>? backoff = null,
        int? maxConcurrency = null,
        SignalSink? signals = null)
        : base(CreateRetryCoordinator(body, maxAttempts, backoff, maxConcurrency, signals))
    {
    }

    private static EphemeralWorkCoordinator<T> CreateRetryCoordinator(
        Func<T, CancellationToken, Task> body,
        int maxAttempts,
        Func<int, TimeSpan>? backoff,
        int? maxConcurrency,
        SignalSink? signals)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var backoffFunc = backoff ?? (attempt => TimeSpan.FromMilliseconds(50 * attempt));

        return new EphemeralWorkCoordinator<T>(
            async (item, ct) =>
            {
                var attempt = 0;
                while (true)
                    try
                    {
                        await body(item, ct).ConfigureAwait(false);
                        return;
                    }
                    catch when (++attempt < maxAttempts && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(backoffFunc(attempt), ct).ConfigureAwait(false);
                    }
            },
            new EphemeralOptions
            {
                MaxConcurrency = ConcurrencyHelper.ResolveDefaultConcurrency(maxConcurrency),
                Signals = signals
            });
    }

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