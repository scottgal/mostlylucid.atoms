# Mostlylucid.Ephemeral.Atoms.Retry

Wraps work with bounded retry and exponential backoff semantics.

## Features

- **Bounded retries**: Configurable max attempts
- **Custom backoff**: Provide your own backoff strategy
- **Default exponential**: 50ms, 100ms, 150ms... by default

## Usage

```csharp
await using var atom = new RetryAtom<HttpRequest>(
    async (req, ct) => await SendAsync(req, ct),
    maxAttempts: 3,
    backoff: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)));

// Automatically retries on failure
await atom.EnqueueAsync(new HttpRequest("https://api.example.com"));

await atom.DrainAsync();
```

## API

| Parameter | Description |
|-----------|-------------|
| `maxAttempts` | Maximum retry attempts (default: 3) |
| `backoff` | Function returning delay for attempt N |
| `maxConcurrency` | Max parallel operations |

## Default Backoff

```
Attempt 1: 50ms
Attempt 2: 100ms
Attempt 3: 150ms
```

## License

MIT
