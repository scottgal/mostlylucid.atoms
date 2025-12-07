# Mostlylucid.Ephemeral

**Fire... and Don't *Quite* Forget.**

A lightweight .NET library for bounded, observable, self-cleaning async execution with signal-based coordination. Targets .NET 6.0, 7.0, 8.0, 9.0, and 10.0.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## The Problem

Modern async code often falls into two extremes:

| Approach | Problem |
|----------|---------|
| **Fire-and-forget** `_ = Task.Run(...)` | No visibility, debugging nightmare, memory leaks |
| **Full await** `await task` | Blocks caller, can't proceed until complete |

## The Solution

Ephemeral provides **trackable, bounded, observable async execution**:

- **Bounded concurrency** - Control how many operations run in parallel
- **Observable window** - See recent operations (running, completed, failed)
- **Self-cleaning** - Old operations automatically evict (no memory leaks)
- **Signal-based coordination** - Operations emit signals that influence execution
- **Zero external dependencies** - Core package is dependency-free

## Installation

```bash
# Core library
dotnet add package mostlylucid.ephemeral

# Or get everything
dotnet add package mostlylucid.ephemeral.complete
```

## Quick Start

### One-Shot Parallel Processing

```csharp
using Mostlylucid.Ephemeral;

// Process a collection with bounded parallelism
await items.EphemeralForEachAsync(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });
```

### Long-Lived Work Coordinator

```csharp
// Create a coordinator that stays alive and accepts items over time
await using var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions
    {
        MaxConcurrency = 8,
        MaxTrackedOperations = 200,
        MaxOperationLifetime = TimeSpan.FromMinutes(5)
    });

// Enqueue work items
await coordinator.EnqueueAsync(new WorkItem("data"));

// See what's happening
var running = coordinator.GetRunning();    // Currently executing
var failed = coordinator.GetFailed();       // Recent failures
var pending = coordinator.PendingCount;     // Waiting in queue

// Graceful shutdown
coordinator.Complete();
await coordinator.DrainAsync();
```

### Per-Key Sequential Processing

Ensure items with the same key are processed in order:

```csharp
await using var coordinator = new EphemeralKeyedWorkCoordinator<Order, string>(
    order => order.CustomerId,  // Key selector
    async (order, ct) => await ProcessOrder(order, ct),
    new EphemeralOptions
    {
        MaxConcurrency = 16,      // Total concurrent operations
        MaxConcurrencyPerKey = 1  // Sequential per customer
    });

// Orders for same customer are processed in order
await coordinator.EnqueueAsync(order1);  // Customer A
await coordinator.EnqueueAsync(order2);  // Customer A - waits for order1
await coordinator.EnqueueAsync(order3);  // Customer B - runs in parallel
```

### Capturing Results

```csharp
await using var coordinator = new EphemeralResultCoordinator<Request, Response>(
    async (req, ct) => await FetchAsync(req, ct),
    new EphemeralOptions { MaxConcurrency = 4 });

var id = await coordinator.EnqueueAsync(new Request("https://api.example.com"));

// Get result when ready
var snapshot = await coordinator.WaitForResult(id);
if (snapshot.HasResult)
    Console.WriteLine(snapshot.Result);
```

## Coordinator Selection Guide

| Scenario | Coordinator |
|----------|-------------|
| Process collection once | `items.EphemeralForEachAsync(...)` |
| Long-lived queue | `EphemeralWorkCoordinator<T>` |
| Per-entity ordering | `EphemeralKeyedWorkCoordinator<TKey, T>` |
| Capture results | `EphemeralResultCoordinator<TInput, TResult>` |
| Multiple priority levels | `PriorityWorkCoordinator<T>` |

## Signals

Operations emit signals that provide cross-cutting observability:

```csharp
// Option 1: Callback on each signal
var options = new EphemeralOptions
{
    OnSignal = evt => Console.WriteLine($"Signal: {evt.Signal} from {evt.OperationId}")
};

// Option 2: Async signal handling (non-blocking)
var options = new EphemeralOptions
{
    OnSignalAsync = async (evt, ct) => await LogToExternalService(evt, ct)
};

// Query signals from the coordinator
bool hasErrors = coordinator.HasSignal("error");
int errorCount = coordinator.CountSignals("error");
var allErrors = coordinator.GetSignalsByPattern("error.*");
```

### Signal-Based Control Flow

```csharp
// Cancel new work when certain signals are present
var options = new EphemeralOptions
{
    CancelOnSignals = new HashSet<string> { "circuit-open", "shutting-down" }
};

// Defer new work when backpressure signals are present
var options = new EphemeralOptions
{
    DeferOnSignals = new HashSet<string> { "backpressure", "rate-limited" },
    DeferCheckInterval = TimeSpan.FromMilliseconds(100),
    MaxDeferAttempts = 50
};
```

### Shared Signal Sink

Share signals across coordinators:

```csharp
var sink = new SignalSink();

var coordinator1 = new EphemeralWorkCoordinator<WorkA>(
    async (a, ct) => { /* work */ },
    new EphemeralOptions { Signals = sink });

var coordinator2 = new EphemeralWorkCoordinator<WorkB>(
    async (b, ct) => { /* work */ },
    new EphemeralOptions { Signals = sink });

// Both coordinators see signals raised by either
sink.Raise("system.busy");
```

## Configuration Options

```csharp
new EphemeralOptions
{
    // Concurrency control
    MaxConcurrency = 8,                    // Max parallel operations
    MaxConcurrencyPerKey = 1,              // For keyed coordinators
    EnableDynamicConcurrency = false,      // Allow runtime adjustment

    // Memory management
    MaxTrackedOperations = 200,            // Window size (LRU eviction)
    MaxOperationLifetime = TimeSpan.FromMinutes(5),  // Age-based eviction

    // Fair scheduling (keyed coordinators)
    EnableFairScheduling = false,          // Prevent hot keys starving cold keys
    FairSchedulingThreshold = 10,          // Deprioritize after N pending

    // Signals
    Signals = sharedSink,                  // Shared signal sink
    OnSignal = evt => { },                 // Sync callback
    OnSignalAsync = async (evt, ct) => { }, // Async callback
    CancelOnSignals = new HashSet<string>(),
    DeferOnSignals = new HashSet<string>(),

    // Signal handler limits
    MaxConcurrentSignalHandlers = 4,
    MaxQueuedSignals = 1000
}
```

## Dependency Injection

```csharp
// Register in Startup/Program.cs
services.AddEphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

// Named coordinators
services.AddEphemeralWorkCoordinator<WorkItem>("priority",
    async (item, ct) => await ProcessPriorityAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 4 });

// Inject and use
public class MyService
{
    private readonly IEphemeralCoordinatorFactory<WorkItem> _factory;

    public MyService(IEphemeralCoordinatorFactory<WorkItem> factory)
    {
        _factory = factory;
    }

    public async Task DoWork()
    {
        var coordinator = _factory.CreateCoordinator();
        await coordinator.EnqueueAsync(new WorkItem());
    }
}
```

## Package Ecosystem

### Core

| Package | Description |
|---------|-------------|
| `mostlylucid.ephemeral` | Core coordinators, signals, and options |

### Atoms (Composable Building Blocks)

Small, opinionated wrappers for common patterns:

| Package | Description |
|---------|-------------|
| `mostlylucid.ephemeral.atoms.fixedwork` | Fixed-concurrency work pipelines with stats |
| `mostlylucid.ephemeral.atoms.keyedsequential` | Per-key sequential processing |
| `mostlylucid.ephemeral.atoms.signalaware` | Pause/cancel intake based on signals |
| `mostlylucid.ephemeral.atoms.batching` | Time/size-based batching before processing |
| `mostlylucid.ephemeral.atoms.retry` | Exponential backoff retry with limits |

### Patterns (Ready-to-Use Compositions)

Production-ready implementations:

| Package | Description |
|---------|-------------|
| `mostlylucid.ephemeral.patterns.circuitbreaker` | Stateless circuit breaker using signal history |
| `mostlylucid.ephemeral.patterns.backpressure` | Signal-driven backpressure (defer on signals) |
| `mostlylucid.ephemeral.patterns.controlledfanout` | Global + per-key gating for controlled parallelism |
| `mostlylucid.ephemeral.patterns.dynamicconcurrency` | Runtime concurrency scaling based on signals |
| `mostlylucid.ephemeral.patterns.adaptiverate` | Signal-driven rate limiting with auto-backoff |
| `mostlylucid.ephemeral.patterns.telemetry` | OpenTelemetry integration |
| `mostlylucid.ephemeral.patterns.anomalydetector` | Moving-window anomaly detection |
| `mostlylucid.ephemeral.patterns.keyedpriorityfanout` | Priority lanes with per-key ordering |
| `mostlylucid.ephemeral.patterns.signalcoordinatedreads` | Quiesce reads during updates |
| `mostlylucid.ephemeral.patterns.reactivefanout` | Two-stage pipeline with backpressure |
| `mostlylucid.ephemeral.patterns.signalinghttp` | HTTP client with progress signals |
| `mostlylucid.ephemeral.patterns.signallogwatcher` | Watch signals for patterns |
| `mostlylucid.ephemeral.patterns.signalreactionshowcase` | Signal dispatch patterns demo |
| `mostlylucid.ephemeral.patterns.longwindowdemo` | Large window configuration demo |

### Demo & Bundles

| Package | Description |
|---------|-------------|
| `mostlylucid.ephemeral.sqlite.singlewriter` | SQLite single-writer demo with mini CQRS |
| `mostlylucid.ephemeral.complete` | All packages in one install |

## Examples

### Circuit Breaker Pattern

```csharp
using Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

var breaker = new SignalBasedCircuitBreaker(
    failureSignal: "api.failure",
    threshold: 5,
    windowSize: TimeSpan.FromSeconds(30));

if (breaker.IsOpen(coordinator))
{
    throw new CircuitOpenException("Too many recent failures");
}

// Proceed with operation
```

### Backpressure Pattern

```csharp
using Mostlylucid.Ephemeral.Patterns.Backpressure;

var sink = new SignalSink();
var coordinator = SignalDrivenBackpressure.Create<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    maxConcurrency: 4);

// When downstream is slow
sink.Raise("backpressure.downstream");  // New work auto-defers

// When recovered
sink.Retract("backpressure.downstream");  // Work resumes
```

### Dynamic Concurrency

```csharp
using Mostlylucid.Ephemeral.Patterns.DynamicConcurrency;

var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    body,
    new EphemeralOptions { EnableDynamicConcurrency = true });

// Scale up when load increases
coordinator.SetMaxConcurrency(16);

// Scale down when throttled
coordinator.SetMaxConcurrency(4);
```

### Batching

```csharp
using Mostlylucid.Ephemeral.Atoms.Batching;

var batcher = new BatchingAtom<LogEntry>(
    async (batch, ct) => await FlushToDatabase(batch, ct),
    batchSize: 100,
    timeout: TimeSpan.FromSeconds(5));  // Flush after 5s even if not full

await batcher.AddAsync(logEntry);  // Batched automatically
```

### Retry with Backoff

```csharp
using Mostlylucid.Ephemeral.Atoms.Retry;

var retryable = new RetryAtom<Request>(
    async (req, ct) => await SendAsync(req, ct),
    maxRetries: 3,
    initialDelay: TimeSpan.FromMilliseconds(100),
    backoffMultiplier: 2.0);

await retryable.ExecuteAsync(request);  // Retries with exponential backoff
```

## Architecture

```
+-------------------------------------------------------------+
|                    Your Application                          |
+-------------------------------------------------------------+
|  Patterns (ready-to-use)     |  Atoms (building blocks)     |
|  - CircuitBreaker            |  - FixedWork                 |
|  - Backpressure              |  - KeyedSequential           |
|  - ControlledFanOut          |  - SignalAware               |
|  - DynamicConcurrency        |  - Batching                  |
|  - AdaptiveRate              |  - Retry                     |
|  - Telemetry                 |                              |
+-------------------------------------------------------------+
|                 mostlylucid.ephemeral (core)                 |
|  - EphemeralWorkCoordinator<T>                               |
|  - EphemeralKeyedWorkCoordinator<TKey, T>                    |
|  - EphemeralResultCoordinator<TInput, TResult>               |
|  - PriorityWorkCoordinator<T>                                |
|  - SignalSink, SignalDispatcher                              |
|  - EphemeralOptions, Snapshots                               |
+-------------------------------------------------------------+
```

## Memory Model

- Operations stored in `ConcurrentQueue<EphemeralOperation>` with bounded window
- `MaxTrackedOperations` limits window size; old operations evict automatically (LRU)
- `MaxOperationLifetime` controls how long completed operations stay visible
- Pinning (`Pin(id)`) prevents eviction for important operations

## Key Concepts

### Operation Lifecycle

```
Enqueued -> Pending -> Running -> Completed/Faulted -> Evicted
                        |
                        +-> Emits signals during execution
```

### Signal Flow

```
Operation raises signal
    |
    v
OnSignal callback (sync)  -->  OnSignalAsync (background queue)
    |
    v
SignalSink (shared state)
    |
    v
CancelOnSignals / DeferOnSignals (affects intake)
```

## Performance Considerations

- **Hot path optimized**: Core coordinator avoids allocations in steady-state
- **Lock-free reads**: Statistics and signal queries don't block writers
- **Throttled cleanup**: Age-based eviction runs periodically, not on every operation
- **Async signal handlers**: Non-blocking I/O for external integrations

## Target Frameworks

- .NET 6.0
- .NET 7.0
- .NET 8.0
- .NET 9.0
- .NET 10.0 (preview)

## License

MIT

## Contributing

Contributions welcome! Please open an issue or PR at [GitHub](https://github.com/scottgal/mostlylucid.ephemeral).
