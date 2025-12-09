using System.Globalization;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.RateLimit;

/// <summary>
/// Signal-driven GCRA rate limiter atom that integrates with Ephemeral's signal system.
///
/// Unlike token bucket which refills periodically, GCRA provides smooth rate limiting
/// by calculating theoretical arrival times for each request.
///
/// Responds to control signals:
/// - "rate.limit.gcra.set:{rate}" - Set rate per second
/// - "rate.limit.gcra.burst:{size}" - Set burst size
/// - "rate.limit.gcra.reset" - Reset accumulated delay
///
/// Emits signals:
/// - "rate.limit.gcra.allowed" - Request was allowed
/// - "rate.limit.gcra.delayed:{ms}" - Request delayed (with delay in ms)
/// - "rate.limit.gcra.denied" - Request denied (when using TryAcquire)
/// - "rate.limit.gcra.config:{rate},{burst}" - Configuration changed
/// </summary>
public sealed class GcraRateLimitAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly string _controlPattern;
    private readonly object _sync = new();
    private readonly IDisposable _subscription;
    private GcraRateLimiter _limiter;
    private bool _emitSignals;

    /// <summary>
    /// Creates a GCRA rate limit atom.
    /// </summary>
    public GcraRateLimitAtom(SignalSink signals, GcraRateLimitOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        options ??= new GcraRateLimitOptions();

        _controlPattern = options.ControlSignalPattern;
        _emitSignals = options.EmitSignals;

        _limiter = new GcraRateLimiter(
            Math.Max(1, options.InitialRatePerSecond),
            Math.Max(1, options.InitialBurstSize));

        _subscription = _signals.Subscribe(OnSignal);

        if (_emitSignals)
        {
            _signals.Raise($"rate.limit.gcra.config:{_limiter.RatePerSecond},{_limiter.BurstSize}");
        }
    }

    /// <summary>
    /// Current configured throughput (requests per second).
    /// </summary>
    public double RatePerSecond => _limiter.RatePerSecond;

    /// <summary>
    /// Current burst size.
    /// </summary>
    public int BurstSize => _limiter.BurstSize;

    /// <summary>
    /// Attempts to acquire a permit without waiting. Returns true if allowed.
    /// Emits "rate.limit.gcra.allowed" or "rate.limit.gcra.denied" signals.
    /// </summary>
    public bool TryAcquire()
    {
        var allowed = _limiter.TryAcquire();

        if (_emitSignals)
        {
            _signals.Raise(allowed ? "rate.limit.gcra.allowed" : "rate.limit.gcra.denied");
        }

        return allowed;
    }

    /// <summary>
    /// Acquires a permit, waiting if necessary.
    /// Emits "rate.limit.gcra.allowed" or "rate.limit.gcra.delayed:{ms}" signals.
    /// </summary>
    public async ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        var delay = await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);

        if (_emitSignals)
        {
            if (delay > TimeSpan.Zero)
            {
                _signals.Raise($"rate.limit.gcra.delayed:{delay.TotalMilliseconds:F2}");
            }
            else
            {
                _signals.Raise("rate.limit.gcra.allowed");
            }
        }
    }

    /// <summary>
    /// Gets the estimated wait time until the next request can be admitted.
    /// </summary>
    public TimeSpan GetEstimatedWaitTime() => _limiter.GetEstimatedWaitTime();

    /// <summary>
    /// Resets accumulated delay (clears the virtual bucket).
    /// </summary>
    public void Reset()
    {
        _limiter.Reset();

        if (_emitSignals)
        {
            _signals.Raise("rate.limit.gcra.reset");
        }
    }

    private void OnSignal(SignalEvent signal)
    {
        if (string.IsNullOrEmpty(_controlPattern) || !StringPatternMatcher.Matches(signal.Signal, _controlPattern))
            return;

        // Set rate: "rate.limit.gcra.set:{rate}"
        if (SignalCommandMatch.TryParse(signal.Signal, "rate.limit.gcra.set", out var match) &&
            TryParseDouble(match.Payload, out var parsed) && parsed > 0)
        {
            UpdateRate(parsed, null);
            return;
        }

        // Set burst: "rate.limit.gcra.burst:{size}"
        if (SignalCommandMatch.TryParse(signal.Signal, "rate.limit.gcra.burst", out match) &&
            TryParseDouble(match.Payload, out parsed) && parsed >= 1)
        {
            UpdateRate(null, (int)parsed);
            return;
        }

        // Reset: "rate.limit.gcra.reset"
        if (signal.Signal.Equals("rate.limit.gcra.reset", StringComparison.OrdinalIgnoreCase))
        {
            Reset();
        }
    }

    private static bool TryParseDouble(string? rawValue, out double value)
    {
        value = 0;
        return rawValue is not null &&
               double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               (value = parsed) == parsed;
    }

    private void UpdateRate(double? ratePerSecond, int? burstSize)
    {
        lock (_sync)
        {
            var newRate = ratePerSecond ?? _limiter.RatePerSecond;
            var newBurst = burstSize ?? _limiter.BurstSize;

            var oldLimiter = _limiter;
            _limiter = new GcraRateLimiter(newRate, newBurst);
            oldLimiter.Dispose();
        }

        if (_emitSignals)
        {
            _signals.Raise($"rate.limit.gcra.config:{_limiter.RatePerSecond},{_limiter.BurstSize}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        _limiter.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Configuration options for GcraRateLimitAtom.
/// </summary>
public sealed class GcraRateLimitOptions
{
    /// <summary>
    /// Initial rate limit (requests per second). Default: 10.
    /// </summary>
    public double InitialRatePerSecond { get; init; } = 10;

    /// <summary>
    /// Initial burst size (immediate requests allowed). Default: 5.
    /// </summary>
    public int InitialBurstSize { get; init; } = 5;

    /// <summary>
    /// Pattern for control signals. Default: "rate.limit.gcra.*"
    /// </summary>
    public string ControlSignalPattern { get; init; } = "rate.limit.gcra.*";

    /// <summary>
    /// Whether to emit signals on acquire/deny/config changes. Default: true.
    /// </summary>
    public bool EmitSignals { get; init; } = true;
}
