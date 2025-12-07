# Mostlylucid.Ephemeral

A lightweight .NET library for bounded, observable, self-cleaning async execution with signal-based coordination.

**Fire... and Don't *Quite* Forget.**

## The Problem

Modern async code often falls into two extremes:
- **Fire-and-forget** (`_ = Task.Run(...)`) - No visibility, debugging nightmare
- **Full await** (`await task`) - Blocks the caller, can't proceed until complete

## The Solution

Mostlylucid.Ephemeral provides a middle ground: **trackable, bounded, observable async execution**.

- **Bounded concurrency** - Control how many operations run in parallel
- **Observable window** - See recent operations (running, completed, failed)
- **Self-cleaning** - Old operations automatically evict (no memory leaks)
- **Signal-based coordination** - Operations emit signals that influence execution

## Quick Start

```csharp
// One-shot parallel processing
await items.EphemeralForEachAsync(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

// Long-lived coordinator
var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

await coordinator.EnqueueAsync(new WorkItem("data"));

// See what's happening
var running = coordinator.GetRunning();
var failed = coordinator.GetFailed();
```

## Coordinator Types

| Scenario | Use |
|----------|-----|
| Process collection once | `items.EphemeralForEachAsync(...)` |
| Long-lived queue | `EphemeralWorkCoordinator<T>` |
| Per-entity ordering | `EphemeralKeyedWorkCoordinator<TKey, T>` |
| Capture results | `EphemeralResultCoordinator<TInput, TResult>` |

## Signals

Operations can emit signals that influence the coordinator:

```csharp
// Emit signals from operation body (via options callback)
var options = new EphemeralOptions
{
    OnSignal = evt => Console.WriteLine($"Signal: {evt.Signal}")
};

// Query signals
var hasError = coordinator.HasSignal("error");
var errorSignals = coordinator.GetSignalsByPattern("error.*");

// Cancel intake on signals
var options = new EphemeralOptions
{
    CancelOnSignals = new HashSet<string> { "circuit-open" }
};
```

## DI Integration

```csharp
services.AddEphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });
```

## Related Packages

- **Atoms** - Small, opinionated wrappers (fixedwork, batching, retry, etc.)
- **Patterns** - Complete usage patterns (circuit breaker, backpressure, etc.)

## License

MIT
