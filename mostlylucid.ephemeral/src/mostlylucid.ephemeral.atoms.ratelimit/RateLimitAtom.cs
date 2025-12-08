using System.Globalization;
using System.Threading.RateLimiting;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.RateLimit;

/// <summary>
/// Controls a coordinator window by gating operations with a token bucket.
/// </summary>
public sealed class RateLimitAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly string _controlPattern;
    private readonly object _sync = new();
    private TokenBucketRateLimiter _limiter;
    private double _ratePerSecond;
    private int _burst;

    /// <summary>
    /// Creates a rate limit atom.
    /// </summary>
    public RateLimitAtom(SignalSink signals, RateLimitOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        options ??= new RateLimitOptions();
        _controlPattern = options.ControlSignalPattern;
        _ratePerSecond = Math.Max(1, options.InitialRatePerSecond);
        _burst = Math.Max(1, options.Burst);
        _limiter = CreateLimiter(_ratePerSecond, _burst);
        _signals.SignalRaised += OnSignal;
    }

    /// <summary>
    /// Current configured throughput (tokens per second).
    /// </summary>
    public double RatePerSecond => Volatile.Read(ref _ratePerSecond);

    /// <summary>
    /// Current burst size (token bucket capacity).
    /// </summary>
    public int Burst => Volatile.Read(ref _burst);

    /// <summary>
    /// Acquires the requested tokens; await before starting work.
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _limiter).AcquireAsync(1, cancellationToken);

    private void OnSignal(SignalEvent signal)
    {
        if (string.IsNullOrEmpty(_controlPattern) || !StringPatternMatcher.Matches(signal.Signal, _controlPattern))
            return;

        if (SignalCommandMatch.TryParse(signal.Signal, "rate.limit.set", out var match) &&
            TryParseDouble(match.Payload, out var parsed) && parsed > 0)
        {
            UpdateRate(parsed);
            return;
        }

        if (SignalCommandMatch.TryParse(signal.Signal, "rate.limit.increase", out match) &&
            TryParseDouble(match.Payload, out parsed))
        {
            UpdateRate(RatePerSecond + Math.Max(1, parsed));
            return;
        }

        if (SignalCommandMatch.TryParse(signal.Signal, "rate.limit.decrease", out match) && TryParseDouble(match.Payload, out parsed))
        {
            UpdateRate(Math.Max(1, RatePerSecond - Math.Max(1, parsed)));
            return;
        }

        if (SignalCommandMatch.TryParse(signal.Signal, "rate.limit.burst", out match) && TryParseDouble(match.Payload, out parsed) && parsed > 0)
        {
            UpdateBurst((int)Math.Max(1, parsed));
        }
    }

    private static bool TryParseDouble(string? rawValue, out double value)
    {
        value = 0;
        return rawValue is not null &&
               double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               (value = parsed) == parsed;
    }

    private void UpdateRate(double rate)
    {
        lock (_sync)
        {
            _ratePerSecond = rate;
            SwapLimiter();
        }

        _signals.Raise("rate.limit.applied");
    }

    private void UpdateBurst(int burst)
    {
        lock (_sync)
        {
            _burst = burst;
            SwapLimiter();
        }

        _signals.Raise("rate.limit.burst.applied");
    }

    private void SwapLimiter()
    {
        var newLimiter = CreateLimiter(_ratePerSecond, _burst);
        var oldLimiter = Interlocked.Exchange(ref _limiter, newLimiter);
        oldLimiter?.Dispose();
    }

    private static TokenBucketRateLimiter CreateLimiter(double ratePerSecond, int burst)
    {
        var tokensPerPeriod = Math.Max(1, (int)Math.Round(ratePerSecond));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue,
            AutoReplenishment = true,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = tokensPerPeriod
        });
    }

    public ValueTask DisposeAsync()
    {
        _signals.SignalRaised -= OnSignal;
        _limiter.Dispose();
        return default;
    }
}
