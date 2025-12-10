namespace Mostlylucid.Ephemeral;

/// <summary>
///     Pattern-based signal filtering for sink subscriptions.
///     Instead of coordinators pushing to multiple sinks (references),
///     sinks pull from coordinators based on patterns (signal-based).
/// </summary>
/// <example>
/// <code>
/// var mainSink = new SignalSink();
/// var errorSink = new SignalSink();
/// var telemetrySink = new SignalSink();
///
/// var coordinator = new EphemeralWorkCoordinator&lt;int&gt;(
///     async (item, ct) =&gt; { /* ... */ },
///     new EphemeralOptions { Signals = mainSink }
/// );
///
/// // Sinks subscribe to specific patterns from the main sink
/// errorSink.SubscribeToPattern(mainSink, "error.*");
/// telemetrySink.SubscribeToPattern(mainSink, "telemetry.*");
/// </code>
/// </example>
public sealed class SignalSubscription
{
    /// <summary>
    ///     Pattern to match for this subscription.
    ///     Supports glob-style wildcards (* and ?).
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    ///     Optional transform/filter function applied before forwarding.
    ///     Return (signal, true) to forward, or (default, false) to suppress.
    /// </summary>
    public Func<SignalEvent, (SignalEvent signal, bool forward)>? Transform { get; }

    /// <summary>
    ///     Optional callback when a signal is forwarded via this subscription.
    /// </summary>
    public Action<SignalEvent>? OnForwarded { get; }

    private readonly IDisposable _subscription;

    internal SignalSubscription(
        string pattern,
        IDisposable subscription,
        Func<SignalEvent, (SignalEvent, bool)>? transform = null,
        Action<SignalEvent>? onForwarded = null)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
        Transform = transform;
        OnForwarded = onForwarded;
    }

    /// <summary>
    ///     Unsubscribe - stops forwarding signals matching this pattern.
    /// </summary>
    public void Unsubscribe()
    {
        _subscription.Dispose();
    }

    /// <summary>
    ///     Internal: Handle an incoming signal event.
    ///     Returns (signal, true) to forward, or (default, false) to suppress.
    /// </summary>
    internal (SignalEvent signal, bool forward) HandleSignal(SignalEvent signal)
    {
        if (!StringPatternMatcher.Matches(signal.Signal, Pattern))
            return (default, false);

        if (Transform != null)
        {
            var (transformed, forward) = Transform(signal);
            if (forward)
            {
                OnForwarded?.Invoke(transformed);
            }
            return (transformed, forward);
        }

        OnForwarded?.Invoke(signal);
        return (signal, true);
    }
}

/// <summary>
///     Extension methods for pattern-based signal routing between sinks.
/// </summary>
public static class SignalRoutingExtensions
{
    /// <summary>
    ///     Subscribe this sink to signals from another sink matching a pattern.
    ///     Signals matching the pattern will be automatically forwarded.
    /// </summary>
    /// <param name="targetSink">Sink that will receive forwarded signals.</param>
    /// <param name="sourceSink">Sink to subscribe to.</param>
    /// <param name="pattern">Pattern to match (glob-style with * and ?).</param>
    /// <param name="transform">Optional transform function.</param>
    /// <param name="onForwarded">Optional callback when signal is forwarded.</param>
    /// <returns>Subscription handle (call Unsubscribe() to stop).</returns>
    public static SignalSubscription SubscribeToPattern(
        this SignalSink targetSink,
        SignalSink sourceSink,
        string pattern,
        Func<SignalEvent, (SignalEvent, bool)>? transform = null,
        Action<SignalEvent>? onForwarded = null)
    {
        if (targetSink == null) throw new ArgumentNullException(nameof(targetSink));
        if (sourceSink == null) throw new ArgumentNullException(nameof(sourceSink));
        if (string.IsNullOrEmpty(pattern)) throw new ArgumentNullException(nameof(pattern));

        // Create subscription object first (we'll pass the disposable later)
        SignalSubscription subscription = null!;

        // Subscribe to source sink and capture the IDisposable
        var disposable = sourceSink.Subscribe(signal =>
        {
            (SignalEvent forwarded, bool shouldForward) = subscription.HandleSignal(signal);
            if (shouldForward)
            {
                targetSink.Raise(forwarded);
            }
        });

        // Now create the subscription with the disposable
        subscription = new SignalSubscription(
            pattern,
            disposable,
            transform,
            onForwarded
        );

        return subscription;
    }
}
