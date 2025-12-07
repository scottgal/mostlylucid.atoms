# Mostlylucid.Ephemeral.Atoms.Retry

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.retry.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry)

Exponential backoff retry for transient failures.

```bash
dotnet add package mostlylucid.ephemeral.atoms.retry
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.Retry;

await using var atom = new RetryAtom<ApiRequest>(
    async (req, ct) => await CallExternalApi(req, ct),
    maxAttempts: 3);

// Automatically retries on failure
await atom.EnqueueAsync(new ApiRequest("https://api.example.com"));

await atom.DrainAsync();
```

---

## All Options

```csharp
new RetryAtom<T>(
    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Max attempts including initial
    // Default: 3
    maxAttempts: 3,

    // Backoff function (attempt -> delay)
    // Default: 50ms * attempt
    backoff: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),

    // Max concurrent operations
    // Default: Environment.ProcessorCount
    maxConcurrency: 4,

    // Shared signal sink
    // Default: null
    signals: sharedSink
)
```

---

## API Reference

```csharp
// Enqueue with automatic retry
ValueTask<long> id = await atom.EnqueueAsync(item, ct);

// Drain
await atom.DrainAsync(ct);

await atom.DisposeAsync();
```

---

## Backoff Strategies

### Default (Linear)
```csharp
// 50ms, 100ms, 150ms
new RetryAtom<T>(body, maxAttempts: 3);
```

### Exponential
```csharp
// 100ms, 200ms, 400ms, 800ms
new RetryAtom<T>(body, maxAttempts: 5,
    backoff: n => TimeSpan.FromMilliseconds(100 * Math.Pow(2, n)));
```

### Fixed
```csharp
// Always 500ms
new RetryAtom<T>(body, maxAttempts: 3,
    backoff: _ => TimeSpan.FromMilliseconds(500));
```

### With Jitter
```csharp
var rng = new Random();
new RetryAtom<T>(body, maxAttempts: 5,
    backoff: n =>
    {
        var baseMs = 100 * Math.Pow(2, n);
        return TimeSpan.FromMilliseconds(baseMs + rng.Next(0, (int)(baseMs * 0.3)));
    });
```

---

## Example: HTTP Calls

```csharp
await using var atom = new RetryAtom<HttpRequest>(
    async (req, ct) =>
    {
        var response = await httpClient.SendAsync(req.Message, ct);
        response.EnsureSuccessStatusCode();
    },
    maxAttempts: 3,
    backoff: n => TimeSpan.FromSeconds(Math.Pow(2, n)),
    maxConcurrency: 8);

foreach (var request in requests)
    await atom.EnqueueAsync(request);

await atom.DrainAsync();
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Circuit breaker |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

MIT
