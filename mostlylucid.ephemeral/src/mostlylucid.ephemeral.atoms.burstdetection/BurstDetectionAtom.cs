using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.BurstDetection;

/// <summary>
///     Detects sudden bursts/spikes in request patterns.
///
///     Tracks requests within a sliding time window and detects when
///     the count exceeds the threshold, indicating a burst pattern.
///
///     Privacy-preserving: Uses XxHash64 for identity hashing.
/// </summary>
public class BurstDetectionAtom : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TrackedRequests> _requests = new();
    private readonly TimeSpan _window;
    private readonly int _threshold;
    private readonly SignalSink? _signals;
    private readonly string _salt;
    private readonly Timer? _cleanupTimer;

    public BurstDetectionAtom(
        TimeSpan? window = null,
        int threshold = 10,
        SignalSink? signals = null,
        string? salt = null)
    {
        _window = window ?? TimeSpan.FromSeconds(30);
        _threshold = threshold;
        _signals = signals;
        _salt = salt ?? Guid.NewGuid().ToString();

        _cleanupTimer = new Timer(CleanupOldEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    ///     Record a request and check for burst in one call.
    /// </summary>
    public BurstResult RecordAndCheck(string identityKey)
    {
        var signature = HashIdentity(identityKey);
        var tracked = _requests.GetOrAdd(signature, _ => new TrackedRequests());

        lock (tracked)
        {
            var now = DateTimeOffset.UtcNow;
            var windowStart = now - _window;

            // Add current request
            tracked.Timestamps.Add(now);

            // Count requests in window
            var recentCount = tracked.Timestamps.Count(t => t >= windowStart);

            // Check for burst
            var isBurst = recentCount >= _threshold;

            if (isBurst)
            {
                // Calculate burst duration (time from first to last request in burst)
                var burstStart = tracked.Timestamps.Where(t => t >= windowStart).Min();
                var burstDuration = now - burstStart;

                _signals?.Raise($"burst.detected:{signature}:count={recentCount}:duration={burstDuration.TotalSeconds:F0}s");

                return new BurstResult
                {
                    IsBurst = true,
                    RequestCount = recentCount,
                    BurstDuration = burstDuration,
                    Description = $"Burst detected: {recentCount} requests in {burstDuration.TotalSeconds:F0}s"
                };
            }

            _signals?.Raise($"burst.normal:{signature}:count={recentCount}");

            return new BurstResult
            {
                IsBurst = false,
                RequestCount = recentCount,
                BurstDuration = TimeSpan.Zero,
                Description = $"Normal pattern: {recentCount} requests in window"
            };
        }
    }

    /// <summary>
    ///     Just record a request without checking for burst.
    /// </summary>
    public void RecordRequest(string identityKey)
    {
        var signature = HashIdentity(identityKey);
        var tracked = _requests.GetOrAdd(signature, _ => new TrackedRequests());

        lock (tracked)
        {
            tracked.Timestamps.Add(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    ///     Get current request count in window for this identity.
    /// </summary>
    public int GetRequestCount(string identityKey)
    {
        var signature = HashIdentity(identityKey);

        if (!_requests.TryGetValue(signature, out var tracked))
            return 0;

        lock (tracked)
        {
            var windowStart = DateTimeOffset.UtcNow - _window;
            return tracked.Timestamps.Count(t => t >= windowStart);
        }
    }

    /// <summary>
    ///     Get request rate (requests per second) for this identity.
    /// </summary>
    public double GetRequestRate(string identityKey)
    {
        var count = GetRequestCount(identityKey);
        return count / _window.TotalSeconds;
    }

    private string HashIdentity(string identityKey)
    {
        var salted = $"{identityKey}:{_salt}";
        var bytes = Encoding.UTF8.GetBytes(salted);
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    private void CleanupOldEntries(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow - _window - TimeSpan.FromMinutes(5);
        var toRemove = new List<string>();

        foreach (var kvp in _requests)
        {
            lock (kvp.Value)
            {
                kvp.Value.Timestamps.RemoveAll(t => t < cutoff);

                if (kvp.Value.Timestamps.Count == 0)
                {
                    toRemove.Add(kvp.Key);
                }
            }
        }

        foreach (var key in toRemove)
        {
            _requests.TryRemove(key, out _);
        }

        if (toRemove.Count > 0)
        {
            _signals?.Raise($"burst.cleanup:removed={toRemove.Count}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleanupTimer != null)
        {
            await _cleanupTimer.DisposeAsync();
        }
    }

    private class TrackedRequests
    {
        public List<DateTimeOffset> Timestamps { get; } = new();
    }
}

/// <summary>
///     Result from burst detection.
/// </summary>
public record BurstResult
{
    public bool IsBurst { get; init; }
    public int RequestCount { get; init; }
    public TimeSpan BurstDuration { get; init; }
    public string Description { get; init; } = string.Empty;
}
