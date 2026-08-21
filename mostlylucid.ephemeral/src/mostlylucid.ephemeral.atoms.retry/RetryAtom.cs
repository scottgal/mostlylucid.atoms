using Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

namespace Mostlylucid.Ephemeral.Atoms.Retry;

/// <summary>
///     Wraps work with retry/backoff semantics using EphemeralWorkCoordinator under the hood.
///     Signal-driven: every attempt failure, backoff wait, exhaustion, and eventual success is
///     raised onto the shared <see cref="SignalSink" /> (when one is supplied) so downstream
///     consumers — including <see cref="SignalBasedCircuitBreaker" /> — can observe real failure
///     history instead of it being invisible inside this atom.
/// </summary>
public sealed class RetryAtom<T> : IAsyncDisposable
{
    /// <summary>Raised on the shared sink each time an attempt fails and another will be tried.</summary>
    public const string AttemptFailedSignal = "retry.attempt.failed";

    /// <summary>Raised on the shared sink when the final attempt fails and no more remain.</summary>
    public const string ExhaustedSignal = "retry.exhausted";

    /// <summary>Raised on the shared sink when an item succeeds after at least one prior failure.</summary>
    public const string SucceededAfterRetrySignal = "retry.succeeded.after.retry";

    private readonly SignalBasedCircuitBreaker? _breaker;
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly SignalSink? _signals;

    /// <param name="body">Arbitrary caller-supplied work.</param>
    /// <param name="backoff">
    ///     Required, no default: delay-per-attempt as caller configuration, not a constant baked
    ///     into this atom. Use <see cref="BackoffStrategies" /> for named strategies
    ///     (Constant/Linear/Exponential/ExponentialWithJitter), or supply your own.
    /// </param>
    /// <param name="maxTotalDuration">
    ///     Required, no default: bounds the entire retry loop for one item — all attempts and
    ///     backoff waits combined — so a caller that never gets this right cannot still reproduce
    ///     an unbounded hang one layer above the core coordinator bound.
    /// </param>
    /// <param name="maxAttempts">Max attempts including the first. Default: 3 (ergonomic, not safety-critical).</param>
    /// <param name="maxConcurrency">Max concurrent operations. Default: ProcessorCount.</param>
    /// <param name="signals">
    ///     Shared signal sink. When set, this atom raises <see cref="AttemptFailedSignal" />,
    ///     <see cref="ExhaustedSignal" />, and <see cref="SucceededAfterRetrySignal" /> — without a
    ///     sink, retries happen but are invisible to everything else, same as before this fix.
    /// </param>
    /// <param name="breaker">
    ///     Optional circuit breaker gating intake. When supplied (requires <paramref name="signals" />
    ///     to be set too, since the breaker reads failure history from the coordinator's signal
    ///     window), <see cref="EnqueueAsync" /> throws <see cref="CircuitOpenException" /> instead of
    ///     enqueueing while the breaker is open.
    /// </param>
    /// <param name="timeProvider">Optional clock for backoff waits; defaults to <see cref="TimeProvider.System"/>.</param>
    public RetryAtom(
        Func<T, CancellationToken, Task> body,
        Func<int, TimeSpan> backoff,
        TimeSpan maxTotalDuration,
        int maxAttempts = 3,
        int? maxConcurrency = null,
        SignalSink? signals = null,
        SignalBasedCircuitBreaker? breaker = null,
        TimeProvider? timeProvider = null)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        if (backoff is null) throw new ArgumentNullException(nameof(backoff));
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (breaker is not null && signals is null)
            throw new ArgumentException(
                "A circuit breaker reads failure history from the coordinator's signal window; " +
                "supply signals when supplying a breaker.", nameof(breaker));

        _breaker = breaker;
        _signals = signals;
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;

        _coordinator = new EphemeralWorkCoordinator<T>(
            async (item, ct) =>
            {
                var attempt = 0;
                while (true)
                    try
                    {
                        await body(item, ct).ConfigureAwait(false);
                        if (attempt > 0)
                            signals?.Raise(SucceededAfterRetrySignal);
                        return;
                    }
                    catch when (!ct.IsCancellationRequested)
                    {
                        attempt++;
                        if (attempt >= maxAttempts)
                        {
                            signals?.Raise(ExhaustedSignal);
                            throw;
                        }

                        signals?.Raise(AttemptFailedSignal);
                        await Task.Delay(backoff(attempt), effectiveTimeProvider, ct).ConfigureAwait(false);
                    }
            },
            maxTotalDuration,
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
                Signals = signals
            },
            effectiveTimeProvider);
    }

    public ValueTask DisposeAsync()
    {
        return _coordinator.DisposeAsync();
    }

    /// <summary>
    ///     Enqueues an item for retrying work. Throws <see cref="CircuitOpenException" /> instead
    ///     of enqueueing if a breaker was supplied and is currently open.
    /// </summary>
    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        // Gate against the raw sink, not the coordinator: RetryAtom's body has no access to the
        // EphemeralOperation object (the coordinator's body signature is item+token only), so its
        // AttemptFailedSignal/ExhaustedSignal are raised straight onto _signals and never attach
        // to any operation's own signal list — the coordinator-scoped IsOpen() would never see them.
        if (_breaker is not null && _signals is not null && _breaker.IsOpen(_signals))
            throw new CircuitOpenException(
                "Circuit breaker is open; not enqueueing.", _breaker.GetTimeUntilClose(_signals));

        return _coordinator.EnqueueWithIdAsync(item, ct);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }
}
