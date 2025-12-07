# Mostlylucid.Ephemeral.Patterns.AdaptiveRate

Adaptive rate limiting using ephemeral signals for automatic backoff.

## How It Works

When a `rate-limit` or `rate-limit:XXXms` signal is present, new work is automatically deferred. No explicit coordination needed between operations.

## Usage

```csharp
var service = new AdaptiveRateService<ApiRequest>(
    async (req, ct) => await CallApiAsync(req, ct),
    maxConcurrency: 8);

// Automatically backs off when rate-limit signals present
await service.ProcessAsync(request);
```

## Signal Format

- `rate-limit` - Generic rate limit, defer for default interval
- `rate-limit:5000ms` - Rate limit with specific retry-after

## License

MIT
