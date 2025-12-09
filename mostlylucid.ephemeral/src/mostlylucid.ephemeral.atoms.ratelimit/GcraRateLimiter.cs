using System.Diagnostics;

namespace Mostlylucid.Ephemeral.Atoms.RateLimit;

/// <summary>
/// Generic Cell Rate Algorithm (GCRA) rate limiter.
///
/// GCRA is a leaky bucket variant that uses a "theoretical arrival time" (TAT)
/// to track when the next request can be admitted without violating rate limits.
///
/// Unlike token bucket which refills at intervals, GCRA smoothly spreads requests
/// over time by calculating the minimum delay between requests.
///
/// See: https://en.wikipedia.org/wiki/Generic_cell_rate_algorithm
/// Inspired by: https://github.com/boinkor-net/governor
/// </summary>
/// <remarks>
/// GCRA works by maintaining a "theoretical arrival time" (TAT):
/// - Each request increments TAT by the emission interval (1/rate)
/// - TAT represents when the "virtual bucket" will be empty
/// - Requests are allowed if: now >= TAT - burst_capacity
/// - This creates smooth rate limiting without periodic token refills
///
/// Example with rate=10/s, burst=5:
/// - Emission interval = 100ms
/// - Burst capacity = 500ms (5 * 100ms)
/// - If TAT is at time T:
///   - Request at T-400ms: ALLOWED (within burst capacity)
///   - Request at T+100ms: DENIED (would exceed rate)
/// </remarks>
public sealed class GcraRateLimiter : IDisposable
{
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private long _tatTicks;  // Theoretical Arrival Time in Stopwatch ticks
    private long _emissionIntervalTicks;  // Time between each request (1/rate)
    private long _burstCapacityTicks;  // Maximum burst allowance

    private double _ratePerSecond;
    private int _burstSize;

    /// <summary>
    /// Creates a GCRA rate limiter.
    /// </summary>
    /// <param name="ratePerSecond">Maximum sustained rate (requests per second)</param>
    /// <param name="burstSize">Maximum burst size (requests that can be sent immediately)</param>
    public GcraRateLimiter(double ratePerSecond, int burstSize = 1)
    {
        if (ratePerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond), "Rate must be positive");
        if (burstSize < 1)
            throw new ArgumentOutOfRangeException(nameof(burstSize), "Burst size must be at least 1");

        _ratePerSecond = ratePerSecond;
        _burstSize = burstSize;

        UpdateParameters(ratePerSecond, burstSize);

        // Initialize TAT to current time (bucket starts empty)
        _tatTicks = _stopwatch.ElapsedTicks;
    }

    /// <summary>
    /// Current configured rate (requests per second).
    /// </summary>
    public double RatePerSecond
    {
        get { lock (_lock) return _ratePerSecond; }
    }

    /// <summary>
    /// Current configured burst size.
    /// </summary>
    public int BurstSize
    {
        get { lock (_lock) return _burstSize; }
    }

    /// <summary>
    /// Updates rate parameters dynamically.
    /// </summary>
    public void UpdateRate(double ratePerSecond, int? burstSize = null)
    {
        lock (_lock)
        {
            if (ratePerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(ratePerSecond));

            var newBurst = burstSize ?? _burstSize;
            if (newBurst < 1)
                throw new ArgumentOutOfRangeException(nameof(burstSize));

            _ratePerSecond = ratePerSecond;
            _burstSize = newBurst;
            UpdateParameters(ratePerSecond, newBurst);
        }
    }

    /// <summary>
    /// Attempts to acquire a permit. Returns true if allowed, false if rate limited.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = _stopwatch.ElapsedTicks;

            // Calculate the earliest time we can allow this request
            // (TAT - burst capacity = earliest allowed arrival)
            var allowAt = _tatTicks - _burstCapacityTicks;

            if (now < allowAt)
            {
                // Request arrives too early - would exceed rate
                return false;
            }

            // Request is allowed
            // Update TAT: max(TAT, now) + emission_interval
            // This ensures TAT never goes backwards and accounts for idle periods
            _tatTicks = Math.Max(_tatTicks, now) + _emissionIntervalTicks;

            return true;
        }
    }

    /// <summary>
    /// Acquires a permit, waiting if necessary. Returns the delay that was applied.
    /// </summary>
    public async ValueTask<TimeSpan> AcquireAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan? delay = null;

            lock (_lock)
            {
                var now = _stopwatch.ElapsedTicks;
                var allowAt = _tatTicks - _burstCapacityTicks;

                if (now >= allowAt)
                {
                    // Allowed now
                    _tatTicks = Math.Max(_tatTicks, now) + _emissionIntervalTicks;
                    return delay ?? TimeSpan.Zero;
                }

                // Calculate required delay
                var delayTicks = allowAt - now;
                delay = TimeSpan.FromTicks(delayTicks);
            }

            // Wait outside the lock
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
            {
                await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the estimated wait time until the next request can be admitted.
    /// </summary>
    public TimeSpan GetEstimatedWaitTime()
    {
        lock (_lock)
        {
            var now = _stopwatch.ElapsedTicks;
            var allowAt = _tatTicks - _burstCapacityTicks;

            if (now >= allowAt)
                return TimeSpan.Zero;

            return TimeSpan.FromTicks(allowAt - now);
        }
    }

    /// <summary>
    /// Resets the limiter state (clears accumulated delay).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _tatTicks = _stopwatch.ElapsedTicks;
        }
    }

    private void UpdateParameters(double ratePerSecond, int burstSize)
    {
        // Emission interval = time between each request at the target rate
        var emissionIntervalSeconds = 1.0 / ratePerSecond;
        _emissionIntervalTicks = (long)(emissionIntervalSeconds * Stopwatch.Frequency);

        // Burst capacity = how far back in time we can still accept requests
        // This is (burstSize - 1) * emission_interval
        // (burstSize - 1) because the first request doesn't need any accumulated capacity
        _burstCapacityTicks = Math.Max(0, burstSize - 1) * _emissionIntervalTicks;
    }

    public void Dispose()
    {
        _stopwatch.Stop();
    }
}
