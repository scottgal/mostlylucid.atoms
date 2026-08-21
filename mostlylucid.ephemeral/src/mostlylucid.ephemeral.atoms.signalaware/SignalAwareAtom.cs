namespace Mostlylucid.Ephemeral.Atoms.SignalAware;

/// <summary>
///     Atom that pauses or cancels intake based on ambient signals (patterns supported).
/// </summary>
public sealed class SignalAwareAtom<T> : IAsyncDisposable
{
    private readonly HashSet<string> _ambient = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string>? _cancelOn;
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly SignalSink? _signals;

    /// <param name="body">Arbitrary caller-supplied work.</param>
    /// <param name="maxBodyDuration">
    ///     Required, no default: forwarded to the underlying EphemeralWorkCoordinator, which owns
    ///     the concurrency slot and must bound how long a single body may hold it.
    /// </param>
    public SignalAwareAtom(
        Func<T, CancellationToken, Task> body,
        TimeSpan maxBodyDuration,
        IReadOnlySet<string>? cancelOn = null,
        IReadOnlySet<string>? deferOn = null,
        TimeSpan? deferInterval = null,
        int? maxDeferAttempts = null,
        SignalSink? signals = null,
        int? maxConcurrency = null,
        TimeProvider? timeProvider = null)
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
            CancelOnSignals = cancelOn,
            DeferOnSignals = deferOn,
            DeferCheckInterval = deferInterval ?? TimeSpan.FromMilliseconds(100),
            MaxDeferAttempts = maxDeferAttempts ?? 50,
            Signals = signals
        };

        _coordinator = new EphemeralWorkCoordinator<T>(body, maxBodyDuration, options, timeProvider);
        _cancelOn = cancelOn;
        _signals = signals;
    }

    public ValueTask DisposeAsync()
    {
        return _coordinator.DisposeAsync();
    }

    /// <summary>
    ///     Checks both manually-seeded ambient signals (<see cref="Raise" />) and the live shared
    ///     sink (if supplied) before enqueueing. Previously this only checked the manually-seeded
    ///     set, so a real cancel-on signal raised through the actual SignalSink never blocked
    ///     intake here — it only affected already-enqueued items via the coordinator's own
    ///     CancelOnSignals handling.
    /// </summary>
    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        if (_cancelOn is { Count: > 0 })
        {
            foreach (var signal in _ambient)
                if (StringPatternMatcher.MatchesAny(signal, _cancelOn))
                    return ValueTask.FromResult(-1L);

            if (_signals is not null)
                foreach (var evt in _signals.Sense())
                    if (StringPatternMatcher.MatchesAny(evt.Signal, _cancelOn))
                        return ValueTask.FromResult(-1L);
        }

        return _coordinator.EnqueueWithIdAsync(item, ct);
    }

    /// <summary>
    ///     Seed ambient signals without requiring a running operation.
    /// </summary>
    public void Raise(string signal)
    {
        if (!string.IsNullOrWhiteSpace(signal))
            _ambient.Add(signal);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot()
    {
        return _coordinator.GetSnapshot();
    }

    public (int Pending, int Active, int Completed, int Failed) Stats()
    {
        return (_coordinator.PendingCount, _coordinator.ActiveCount, _coordinator.TotalCompleted,
            _coordinator.TotalFailed);
    }
}
