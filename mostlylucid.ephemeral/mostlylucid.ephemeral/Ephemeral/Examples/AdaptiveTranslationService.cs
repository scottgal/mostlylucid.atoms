namespace Mostlylucid.Helpers.Ephemeral.Examples;

/// <summary>
/// Example showing adaptive rate limiting using ephemeral signals.
/// When a rate limit is hit, the signal is visible to all subsequent operations,
/// causing automatic backoff without shared state or message passing.
/// </summary>
public class AdaptiveTranslationService : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<TranslationRequest> _coordinator;
    private readonly ITranslationApi _translationApi;

    public AdaptiveTranslationService(ITranslationApi translationApi)
    {
        _translationApi = translationApi;

        _coordinator = new EphemeralWorkCoordinator<TranslationRequest>(
            ProcessTranslationAsync,
            new EphemeralOptions
            {
                MaxConcurrency = 8,
                MaxTrackedOperations = 100,

                // New work is deferred while any "rate-limit" or "rate-limit:*" signal is present
                DeferOnSignals = new HashSet<string> { "rate-limit", "rate-limit:*" },
                MaxDeferAttempts = 10,
                DeferCheckInterval = TimeSpan.FromMilliseconds(100)
            });
    }

    /// <summary>
    /// Enqueue a translation request. Automatically backs off if rate limits are detected.
    /// </summary>
    public async Task TranslateAsync(TranslationRequest request)
    {
        // Optional: extra politeness based on most recent retry-after
        var rateLimitSignals = _coordinator.GetSignalsByPattern("rate-limit:*");
        if (rateLimitSignals.Count > 0)
        {
            var latest = rateLimitSignals
                .OrderByDescending(s => s.Timestamp)
                .First()
                .Signal; // "rate-limit:5000ms"

            if (TryParseRetryAfter(latest, out var delay))
            {
                await Task.Delay(delay);
            }
        }

        await _coordinator.EnqueueAsync(request);
    }

    private async Task ProcessTranslationAsync(
        TranslationRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _translationApi.TranslateAsync(request, ct);

            if (result.FromCache)
            {
                // Signal available for metrics/monitoring
                // (would need op access - see note below)
            }
        }
        catch (RateLimitException ex)
        {
            // These signals trigger DeferOnSignals for subsequent requests
            // Note: In real usage, you'd emit via the operation's ISignalEmitter
            // The coordinator's ProcessWithSignalsAsync provides this access
            throw;
        }
    }

    /// <summary>
    /// Parse a retry-after signal like "rate-limit:5000ms" to extract the delay.
    /// </summary>
    public static bool TryParseRetryAfter(string signal, out TimeSpan delay)
    {
        delay = default;

        // Expect "rate-limit:5000ms"
        var parts = signal.Split(':', 2);
        if (parts.Length != 2)
            return false;

        var payload = parts[1].Trim(); // "5000ms"
        if (!payload.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            return false;

        var numPart = payload[..^2]; // "5000"
        if (!int.TryParse(numPart, out var ms) || ms < 0)
            return false;

        delay = TimeSpan.FromMilliseconds(ms);
        return true;
    }

    // Expose for testing
    public int PendingCount => _coordinator.PendingCount;
    public int ActiveCount => _coordinator.ActiveCount;
    public long TotalCompleted => _coordinator.TotalCompleted;
    public long TotalFailed => _coordinator.TotalFailed;

    public async ValueTask DisposeAsync()
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync();
        await _coordinator.DisposeAsync();
    }
}

/// <summary>
/// Sample translation request for the example.
/// </summary>
public record TranslationRequest(string Text, string TargetLanguage);

/// <summary>
/// Sample translation result for the example.
/// </summary>
public record TranslationResult(string TranslatedText, bool FromCache);

/// <summary>
/// Interface for translation API (for testing/mocking).
/// </summary>
public interface ITranslationApi
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);
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
