# Mostlylucid.Ephemeral.Patterns.AdaptiveRate

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.adaptiverate.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.adaptiverate)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Adaptive rate limiting using ephemeral signals for automatic backoff.

```bash
dotnet add package mostlylucid.ephemeral.patterns.adaptiverate
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.AdaptiveRate;

await using var service = new AdaptiveRateService<ApiRequest>(
    async (req, ct) => await CallApiAsync(req, ct),
    maxConcurrency: 8);

// Automatically backs off when rate-limit signals present
await service.ProcessAsync(request);
```

---

## All Options

```csharp
new AdaptiveRateService<T>(
    // Required: async work processor
    processAsync: async (item, ct) => await ProcessAsync(item, ct),

    // Max concurrent operations
    // Default: 8
    maxConcurrency: 8
)
```

---

## API Reference

```csharp
// Process item with automatic rate limit handling
await service.ProcessAsync(item);

// Check queue status
int pending = service.PendingCount;
int active = service.ActiveCount;

// Dispose
await service.DisposeAsync();
```

---

## Signal Format

- `rate-limit` - Generic rate limit, defer for default interval
- `rate-limit:5000ms` - Rate limit with specific retry-after in milliseconds

---

## How It Works

When a `rate-limit` or `rate-limit:XXXms` signal is present, new work is automatically deferred. No explicit
coordination needed between operations.

```
[Request] -> Check signals -> [rate-limit:1000ms] -> Wait 1s -> Process
[Request] -> Check signals -> [no signals] -> Process immediately
```

---

## Example: API with Rate Limiting

```csharp
await using var service = new AdaptiveRateService<ApiRequest>(
    async (req, ct) =>
    {
        var response = await httpClient.SendAsync(req.Message, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            // Signal will auto-defer subsequent requests
            throw new RateLimitException("Rate limited", retryAfter);
        }

        response.EnsureSuccessStatusCode();
    },
    maxConcurrency: 4);

foreach (var request in requests)
    await service.ProcessAsync(request);
```

---

## Related Packages

| Package                                                                                                                   | Description          |
|---------------------------------------------------------------------------------------------------------------------------|----------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                             | Core library         |
| [mostlylucid.ephemeral.patterns.backpressure](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure) | Backpressure pattern |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                           | All in one DLL       |

## License

Unlicense (public domain)