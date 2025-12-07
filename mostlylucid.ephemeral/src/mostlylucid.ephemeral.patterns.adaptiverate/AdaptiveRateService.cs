using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.AdaptiveRate;

/// <summary>
/// Adaptive rate limiting using ephemeral signals. When rate-limit signals are present,
/// new work is automatically deferred without explicit coordination.
/// </summary>
public class AdaptiveRateService<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;

    public AdaptiveRateService(
        Func<T, CancellationToken, Task> processAsync,
        int maxConcurrency = 8)
    {
        _coordinator = new EphemeralWorkCoordinator<T>(
            processAsync,
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency,
                MaxTrackedOperations = 100,
                DeferOnSignals = new HashSet<string> { "rate-limit", "rate-limit:*" },
                MaxDeferAttempts = 10,
                DeferCheckInterval = TimeSpan.FromMilliseconds(100)
            });
    }

    public async Task ProcessAsync(T request)
    {
        var rateLimitSignals = _coordinator.GetSignalsByPattern("rate-limit:*");
        if (rateLimitSignals.Count > 0)
        {
            var latest = rateLimitSignals
                .OrderByDescending(s => s.Timestamp)
                .First()
                .Signal;

            if (TryParseRetryAfter(latest, out var delay))
            {
                await Task.Delay(delay);
            }
        }

        await _coordinator.EnqueueAsync(request);
    }

    public static bool TryParseRetryAfter(string signal, out TimeSpan delay)
    {
        delay = default;
        var parts = signal.Split(':', 2);
        if (parts.Length != 2) return false;

        var payload = parts[1].Trim();
        if (!payload.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) return false;

        var numPart = payload[..^2];
        if (!int.TryParse(numPart, out var ms) || ms < 0) return false;

        delay = TimeSpan.FromMilliseconds(ms);
        return true;
    }

    public int PendingCount => _coordinator.PendingCount;
    public int ActiveCount => _coordinator.ActiveCount;

    public async ValueTask DisposeAsync()
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync();
        await _coordinator.DisposeAsync();
    }
}

/// <summary>
/// Exception thrown when API rate limit is exceeded.
/// </summary>
public class RateLimitException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public RateLimitException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }
}
