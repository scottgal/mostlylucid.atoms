namespace Mostlylucid.Ephemeral.Atoms.Escalator;

/// <summary>
///     Promotes typed, ephemeral signals into durable sinks.
/// </summary>
/// <remarks>
///     EscalatorAtom is the membrane between in-memory coordination and persistent substrate updates.
///     It listens to typed signals, applies the escalation filter, and persists to each configured target.
/// </remarks>
public sealed class EscalatorAtom<TPayload> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<SignalEvent<TPayload>> _coordinator;
    private readonly EscalatorAtomOptions<TPayload> _options;
    private readonly SignalSink _signals;
    private readonly IReadOnlyList<EscalationTarget<TPayload>> _targets;
    private readonly TypedSignalSink<TPayload> _typedSignals;
    private bool _disposed;

    /// <summary>
    ///     Initializes an EscalatorAtom with one or more persistence targets.
    /// </summary>
    /// <param name="signals">Signal sink used to emit success or failure signals.</param>
    /// <param name="typedSignals">Typed signal source to observe for escalation.</param>
    /// <param name="targets">Persistence targets to write to.</param>
    /// <param name="options">Optional escalation options and callbacks.</param>
    public EscalatorAtom(
        SignalSink signals,
        TypedSignalSink<TPayload> typedSignals,
        IReadOnlyList<EscalationTarget<TPayload>> targets,
        TimeSpan maxBodyDuration,
        EscalatorAtomOptions<TPayload>? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _typedSignals = typedSignals ?? throw new ArgumentNullException(nameof(typedSignals));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        if (_targets.Count == 0)
            throw new ArgumentException("EscalatorAtom requires at least one target.", nameof(targets));

        _options = options ?? new EscalatorAtomOptions<TPayload>();
        _coordinator = new EphemeralWorkCoordinator<SignalEvent<TPayload>>(
            EscalateAsync,
            maxBodyDuration,
            _options.CoordinatorOptions ?? new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 64,
                MaxOperationLifetime = TimeSpan.FromSeconds(30)
            });

        _typedSignals.TypedSignalRaised += OnTypedSignal;
    }

    /// <summary>
    ///     Stops listening for signals and drains any pending escalations.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _typedSignals.TypedSignalRaised -= OnTypedSignal;
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTypedSignal(SignalEvent<TPayload> evt)
    {
        if (_disposed)
            return;

        var shouldEscalate = _options.ShouldEscalate is not null
            ? _options.ShouldEscalate(evt)
            : StringPatternMatcher.Matches(evt.Signal, _options.EscalateSignalPattern);

        if (!shouldEscalate)
            return;

        _ = _coordinator.EnqueueAsync(evt);
    }

    private async Task EscalateAsync(SignalEvent<TPayload> evt, CancellationToken ct)
    {
        List<Exception>? failures = null;

        try
        {
            foreach (var target in _targets)
                try
                {
                    await target.PersistAsync(evt, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures ??= new List<Exception>();
                    failures.Add(new InvalidOperationException(
                        $"Escalation target '{target.Name}' failed.",
                        ex));
                }

            if (failures is not null)
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);

            _options.OnEscalated?.Invoke(evt);

            if (!string.IsNullOrWhiteSpace(_options.EmitOnSuccess))
                _signals.Raise(_options.EmitOnSuccess!, evt.Key);
        }
        catch (Exception ex)
        {
            _options.OnFailed?.Invoke(evt, ex);

            if (!string.IsNullOrWhiteSpace(_options.EmitOnFailure))
                _signals.Raise($"{_options.EmitOnFailure!}:{ex.GetType().Name}", evt.Key);
        }
    }
}