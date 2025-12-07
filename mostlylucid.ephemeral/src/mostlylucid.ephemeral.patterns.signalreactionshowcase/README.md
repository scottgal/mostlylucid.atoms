# Mostlylucid.Ephemeral.Patterns.SignalReactionShowcase

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.signalreactionshowcase.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalreactionshowcase)

Demonstrates signal emission, async dispatch, and sink polling patterns.

```bash
dotnet add package mostlylucid.ephemeral.patterns.signalreactionshowcase
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalReactionShowcase;

var result = await SignalReactionShowcase.RunAsync(itemCount: 100);

Console.WriteLine($"Dispatched: {result.DispatchedHits}");
Console.WriteLine($"Polled: {result.PolledHits}");
Console.WriteLine($"Total signals: {result.Signals.Count}");
```

---

## All Options

```csharp
SignalReactionShowcase.RunAsync(
    // Number of work items to process
    // Default: 8
    itemCount: 8,

    // Optional cancellation token
    ct: cancellationToken
)
```

---

## API Reference

```csharp
// Run the showcase demo
Task<Result> SignalReactionShowcase.RunAsync(
    int itemCount = 8,
    CancellationToken ct = default);

// Result record
public sealed record Result(
    int DispatchedHits,      // Signals handled by dispatcher
    int PolledHits,          // Signals found by polling
    IReadOnlyList<string> Signals);  // All signal names
```

---

## Key Concepts

### 1. Signal Emission

Work items raise signals synchronously (fast, non-blocking):

```csharp
var signal = new SignalEvent($"stage.start:{item}", id, null, DateTimeOffset.UtcNow);
sink.Raise(signal);
```

### 2. Async Dispatch

SignalDispatcher processes signals in background:

```csharp
dispatcher.Register("stage.done:*", async evt =>
{
    // Handle signal asynchronously
    await LogCompletionAsync(evt);
});

dispatcher.Dispatch(signal);
```

### 3. Sink Polling

Query signals by pattern after work completes:

```csharp
var completed = sink.Sense(s => s.Signal.StartsWith("stage.done"));
```

---

## How It Works

```
Work Item Processing:
┌─────────────────────────────────────────────────────────────┐
│ [start] ─> emit "stage.start:N" ─> [work] ─> emit "stage.done:N" │
└─────────────────────────────────────────────────────────────┘
                    │                              │
                    ▼                              ▼
              SignalSink                    SignalDispatcher
              (for polling)                 (async handlers)
```

---

## Example: Custom Signal Handling

```csharp
var sink = new SignalSink(maxCapacity: 200, maxAge: TimeSpan.FromSeconds(10));
var dispatched = 0;

await using var dispatcher = new SignalDispatcher(
    new EphemeralOptions { MaxTrackedOperations = 200, MaxConcurrency = 4 });

dispatcher.Register("stage.done:*", evt =>
{
    Interlocked.Increment(ref dispatched);
    Console.WriteLine($"Completed: {evt.Signal}");
    return Task.CompletedTask;
});

await using var coordinator = new EphemeralWorkCoordinator<int>(
    async (item, ct) =>
    {
        var start = new SignalEvent($"stage.start:{item}", item, null, DateTimeOffset.UtcNow);
        sink.Raise(start);
        dispatcher.Dispatch(start);

        await Task.Delay(10, ct);  // Simulate work

        var done = new SignalEvent($"stage.done:{item}", item, null, DateTimeOffset.UtcNow);
        sink.Raise(done);
        dispatcher.Dispatch(done);
    },
    new EphemeralOptions { MaxConcurrency = 8 });

for (int i = 0; i < 50; i++)
    await coordinator.EnqueueAsync(i);

coordinator.Complete();
await coordinator.DrainAsync();
await dispatcher.FlushAsync();

// Poll results
var startSignals = sink.Sense(s => s.Signal.StartsWith("stage.start")).Count;
var doneSignals = sink.Sense(s => s.Signal.StartsWith("stage.done")).Count;

Console.WriteLine($"Started: {startSignals}, Done: {doneSignals}, Dispatched: {dispatched}");
```

---

## Example: Comparing Dispatch vs Polling

```csharp
var result = await SignalReactionShowcase.RunAsync(itemCount: 100);

// DispatchedHits: Real-time processing via SignalDispatcher
// PolledHits: Batch query via SignalSink.Sense()

// Both should equal itemCount when all work completes
Assert.Equal(result.DispatchedHits, result.PolledHits);
Assert.Equal(100, result.DispatchedHits);
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.signallogwatcher](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher) | Signal log watcher |
| [mostlylucid.ephemeral.patterns.telemetry](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.telemetry) | Telemetry integration |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
