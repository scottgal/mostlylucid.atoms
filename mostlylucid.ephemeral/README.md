# Mostlylucid.Ephemeral

**Fire... and Don't *Quite* Forget.**

A lightweight .NET library for bounded, observable, self-cleaning async execution with signal-based coordination. Targets .NET 6.0, 7.0, 8.0, 9.0, and 10.0.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral)
[![License](https://img.shields.io/badge/license-Unlicense-blue.svg)](UNLICENSE)

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

## Attribute-driven jobs

`mostlylucid.ephemeral.attributes` ships with the core packages and lets you decorate methods with `[EphemeralJob]` or `[EphemeralJobs]` to react to `SignalSink` events declaratively. Load the handlers with `EphemeralSignalJobRunner` and you get the same signal-wave/log-watcher orchestration, just wired through attributes.

```csharp
[EphemeralJobs(SignalPrefix = "stage", DefaultLane = "pipeline")]
public sealed class StageJobs
{
    [EphemeralJob("ingest", EmitOnComplete = new[] { "stage.ingest.done" })]
    public Task IngestAsync(SignalEvent evt) => Console.Out.WriteLineAsync(evt.Signal);

    [EphemeralJob("finalize")]
    public Task FinalizeAsync(SignalEvent evt) => Console.Out.WriteLineAsync("final stage");
}

var sink = new SignalSink();
await using var runner = new EphemeralSignalJobRunner(sink, new[] { new StageJobs() });
sink.Raise("stage.ingest");
```

Attribute jobs are now a core surface—they emit completion/failure signals, support priority/concurrency settings, and plug into the same caches, log adapters, and workflows you already build with signals.

For DI-first setups use `services.AddEphemeralSignalJobRunner<T>()` or `services.AddEphemeralScopedJobRunner<T>()` so the runner and sink are managed by the container.

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

### Signal Orchestration Helpers

- Typed payload signals: `var typed = new TypedSignalSink<BotEvidence>(); typed.Raise("bot.evidence", payload);` (mirrors to the untyped `SignalSink` for compatibility)
- Staged/wave execution: `new SignalWaveExecutor(sink, new[]{ new SignalStage("detect","stage.start", DoWork, EmitOnComplete:new[]{"stage.done"}) }, earlyExitSignals:new[]{"verdict.*"})`
- Quorum/consensus: `await SignalConsensus.WaitForQuorumAsync(sink, "vote.*", required:3, timeout:TimeSpan.FromSeconds(2));`
- Progress pings: `ProgressSignals.Emit(sink, "ingest", current, total, sampleRate:5);`
- Decaying reputation: `var rep = new DecayingReputationWindow<string>(TimeSpan.FromMinutes(5)); rep.Update(userId, +1); var score = rep.GetScore(userId);`
- Log hook: (from `mostlylucid.ephemeral.logging`) `var logSink = new TypedSignalSink<SignalLogPayload>(); using var provider = new SignalLoggerProvider(logSink); loggerFactory.AddProvider(provider);` → emits slugged signals like `log.error.orderservice.db-failure:invalidoperationexception` with typed payload carrying event id, scope data, and exception metadata.
- Signal→log bridge (also part of `mostlylucid.ephemeral.logging`): `using var bridge = new SignalToLoggerAdapter(sink, logger);` lets signals flow back into Microsoft.Extensions.Logging (default level inferred from signal prefix such as `error.*`, `warn.*`, etc.).
- Attribute jobs: `[EphemeralJob("orders.process")]` on a class plus `new EphemeralSignalJobRunner(sink, new[] { new OrderJobs() });` wires the annotated methods into an `EphemeralWorkCoordinator` and runs them whenever the matching signal fires (`mostlylucid.ephemeral.attributes` package).
  - Attribute-driven pipelines: decorate methods with `[EphemeralJob]`, share a `SignalSink`, and load them via `EphemeralSignalJobRunner` to mirror the manual `SignalWaveExecutor` and watcher examples. Example:

```csharp
[EphemeralJobs(SignalPrefix = "stage", DefaultLane = "pipeline")]
public sealed class StageJobs
{
    [EphemeralJob("ingest", EmitOnComplete = new[] { "stage.ingest.done" })]
    public Task IngestAsync(SignalEvent evt, CancellationToken ct)
    {
        Console.WriteLine($"Stage trigger: {evt.Signal}");
        return Task.CompletedTask;
    }

    [EphemeralJob("finalize")]
    public Task FinalizeAsync(SignalEvent evt, CancellationToken ct)
    {
        Console.WriteLine("Final stage complete");
        return Task.CompletedTask;
    }
}

var sink = new SignalSink();
await using var runner = new EphemeralSignalJobRunner(sink, new[] { new StageJobs() });
sink.Raise("stage.ingest");
```

The handler raises `stage.ingest.done` automatically, so downstream jobs can be wired in the same way the other signal helpers emit completion signals.
- Push subscribers: `sink.SignalRaised += evt => ...;` for live tap alongside snapshot APIs.

Quick bot-detection flow (stages + quorum + reputation):

```csharp
var sink = new SignalSink();
var evidence = new TypedSignalSink<BotEvidence>(sink);
var rep = new DecayingReputationWindow<string>(TimeSpan.FromMinutes(10), signals: sink);

// Stage: run detectors when content arrives
await using var waves = new SignalWaveExecutor(
    sink,
    new[]
    {
        new SignalStage("lexical","content.received",
            ct => RunLexicalAsync(ct),
            EmitOnComplete: new[] { "vote.lexical" }),
        new SignalStage("behavior","content.received",
            ct => RunBehaviorAsync(ct),
            EmitOnComplete: new[] { "vote.behavior" })
    },
    earlyExitSignals: new[] { "verdict.*" });
waves.Start();

// Quorum: wait for 2 votes
_ = Task.Run(async () =>
{
    var quorum = await SignalConsensus.WaitForQuorumAsync(sink, "vote.*", required: 2,
        timeout: TimeSpan.FromSeconds(3), cancelOn: new[] { "verdict.*" });
    if (quorum.Reached)
        sink.Raise("verdict.bot");
});

// Reputation bump
rep.Update("user-123", +5); // on evidence

// Emit evidence with payload for audit
evidence.Raise("bot.evidence", new BotEvidence { UserId = "user-123", Score = rep.GetScore("user-123") });
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

+ See also: `docs/Services.md` for a DI-focused guide with examples and best-practices.

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

### Cache Strategies (quick map)

| Cache | Expiration model | Specialization | Where |
|-------|------------------|----------------|-------|
| `SlidingCacheAtom` | Sliding on every hit + absolute max lifetime | Async factory + dedupe; rich signals | `atoms.slidingcache` |
| `EphemeralLruCache` (default) | Sliding on hit; hot keys extend TTL; LRU-style eviction | Hot detection (`cache.hot/evict` signals) | Core + `sqlite.singlewriter` |
| `MemoryCache` (legacy/optional) | Sliding TTL only | No hot tracking or dedupe | Only if you opt out of LRU |

### Sample: Self-optimizing hot-key cache (EphemeralLruCache)

```csharp
using Mostlylucid.Ephemeral;

var cache = new EphemeralLruCache<string, User>(new EphemeralLruCacheOptions
{
    DefaultTtl = TimeSpan.FromMinutes(5),
    HotKeyExtension = TimeSpan.FromMinutes(30),
    HotAccessThreshold = 3, // 3 hits = hot
    MaxSize = 10_000,
    SampleRate = 5 // emit 1 in 5 signals
});

// Read-through with self-optimizing TTLs
async Task<User> GetUserAsync(string id) =>
    await cache.GetOrAddAsync(id, async key =>
    {
        var user = await db.LoadUserAsync(key);
        return user!;
    });

// Observe how the cache self-focuses
var signals = cache.GetSignals().Where(s => s.Signal.StartsWith("cache."));
var stats = cache.GetStats(); // hot/expired counts, size
```

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

## Pin Until Queried: Responsibility Signal

One of the trickiest problems in distributed systems is balancing resource cleanup with clear ownership, so we introduced the **Responsibility Signal**.

1. **Self-declared responsibility.** An atom can emit `pins` when it completes, indicating “I’m still responsible for this result until someone else asks.” It keeps itself alive until a downstream consumer queries it.
2. **Automatic resource management.** Once the consumer has inspected or acknowledged the work, the atom unpins itself and resumes the normal eviction timetable. Nothing disappears too soon, and nothing lingers forever.
3. **Durable yet ephemeral.** You get the durability you need for reliable hand-offs while keeping the window self-cleaning whenever coordination succeeds.

In practice, a file-processing atom might:

1. Emit `file.processed`, set `{ pinned: true, status: "awaiting_query", file_location: "/bucket/uuid" }`, and keep the result alive.
2. Wait for Coordinator B to query the file (maybe via signals or `EphemeralWorkCoordinator`), reply with metadata, and flip the internal state to `{ pinned: false, status: "complete" }`.
3. Finally allow the usual TTL-driven eviction to remove the operation.

This pattern eliminates race conditions (resources announce their availability), creates a self-healing hand-off (pinned work survives coordinator crashes), avoids orphaned resources, and makes for resilient work queues where tasks aren’t lost but also don’t pile up indefinitely. Atoms become autonomous agents, managing their own existence based on responsibility signals.

Use `ResponsibilitySignalManager` with your sink/coordinator to `PinUntilQueried` (default ack signal: `responsibility.ack.*` with key=`operationId`) so the window automatically unpins once the downstream signal appears. Set the optional `description` so the atom can express, “I saved this file, I’m waiting for somebody to see where it landed.” Pass `maxPinDuration` (e.g., `TimeSpan.FromMinutes(5)`) to bound how long pins can extend beyond the normal operation lifetime so the system remains self-cleaning even when queries are delayed.

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

Unlicense (public domain)

## Contributing

Contributions welcome! Please open an issue or PR at [GitHub](https://github.com/scottgal/mostlylucid.atoms).
