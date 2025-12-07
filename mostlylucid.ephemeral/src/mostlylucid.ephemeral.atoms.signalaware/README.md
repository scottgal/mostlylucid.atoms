# Mostlylucid.Ephemeral.Atoms.SignalAware

Atom that pauses or cancels intake based on ambient signals. Perfect for circuit-breaker patterns.

## Features

- **Cancel on signals**: Stop accepting work when certain signals are present
- **Defer on signals**: Wait for signals to clear before starting work
- **Ambient signals**: Raise signals externally without running operations

## Usage

```csharp
await using var atom = new SignalAwareAtom<Request>(
    async (req, ct) => await ProcessAsync(req, ct),
    cancelOn: new HashSet<string> { "circuit-open", "rate-limited" },
    deferOn: new HashSet<string> { "backpressure" });

// Work proceeds normally
await atom.EnqueueAsync(new Request("data"));

// Raise a signal - stops new work
atom.Raise("circuit-open");

// This returns -1 (rejected)
var id = await atom.EnqueueAsync(new Request("more-data"));
if (id == -1) Console.WriteLine("Rejected due to circuit open");

await atom.DrainAsync();
```

## API

| Parameter | Description |
|-----------|-------------|
| `cancelOn` | Signals that cause new work to be rejected |
| `deferOn` | Signals that cause work to wait before starting |
| `deferInterval` | How often to check if defer signals cleared |
| `maxDeferAttempts` | Max times to wait before proceeding anyway |

## License

MIT
