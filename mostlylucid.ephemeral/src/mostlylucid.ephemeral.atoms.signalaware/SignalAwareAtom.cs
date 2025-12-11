namespace Mostlylucid.Ephemeral.Atoms.SignalAware;

/// <summary>
///     Atom that pauses or cancels intake based on ambient signals (patterns supported).
/// </summary>
public sealed class SignalAwareAtom<T> : AtomBase<EphemeralWorkCoordinator<T>>
{
    private readonly HashSet<string> _ambient = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string>? _cancelOn;

    public SignalAwareAtom(
        Func<T, CancellationToken, Task> body,
        IReadOnlySet<string>? cancelOn = null,
        IReadOnlySet<string>? deferOn = null,
        TimeSpan? deferInterval = null,
        int? maxDeferAttempts = null,
        SignalSink? signals = null,
        int? maxConcurrency = null)
        : base(new EphemeralWorkCoordinator<T>(body, new EphemeralOptions
        {
            MaxConcurrency = ConcurrencyHelper.ResolveDefaultConcurrency(maxConcurrency),
            CancelOnSignals = cancelOn,
            DeferOnSignals = deferOn,
            DeferCheckInterval = deferInterval ?? TimeSpan.FromMilliseconds(100),
            MaxDeferAttempts = maxDeferAttempts ?? 50,
            Signals = signals
        }))
    {
        _cancelOn = cancelOn;
    }

    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        if (_cancelOn is { Count: > 0 })
            foreach (var signal in _ambient)
                if (StringPatternMatcher.MatchesAny(signal, _cancelOn))
                    return ValueTask.FromResult(-1L);

        return Coordinator.EnqueueWithIdAsync(item, ct);
    }

    /// <summary>
    ///     Seed ambient signals without requiring a running operation.
    /// </summary>
    public void Raise(string signal)
    {
        if (!string.IsNullOrWhiteSpace(signal))
            _ambient.Add(signal);
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