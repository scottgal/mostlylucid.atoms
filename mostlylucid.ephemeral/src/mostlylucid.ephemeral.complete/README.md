# Mostlylucid.Ephemeral.Complete

**All of Mostlylucid.Ephemeral in a single DLL** - bounded async execution with signal-based coordination.

```bash
dotnet add package mostlylucid.ephemeral.complete
```

This package compiles all core, atom, and pattern code into one assembly. For individual packages, see the links in each section below.

---

## Table of Contents

- [Quick Start](#quick-start)
- [Core Coordinators](#core-coordinators)
- [Configuration](#configuration-ephemeraloptions)
- [Signals](#signals)
- [Atoms](#atoms-building-blocks)
  - [FixedWorkAtom](#fixedworkatom)
  - [KeyedSequentialAtom](#keyedsequentialatom)
  - [SignalAwareAtom](#signalawareatom)
  - [BatchingAtom](#batchingatom)
  - [RetryAtom](#retryatom)
- [Patterns](#patterns-ready-to-use)
  - [SignalBasedCircuitBreaker](#signalbasedcircuitbreaker)
  - [SignalDrivenBackpressure](#signaldrivenbackpressure)
  - [ControlledFanOut](#controlledfanout)
  - [AdaptiveRateService](#adaptiverateservice)
  - [DynamicConcurrencyDemo](#dynamicconcurrencydemo)
  - [KeyedPriorityFanOut](#keyedpriorityfanout)
  - [ReactiveFanOutPipeline](#reactivefanoutpipeline)
  - [SignalAnomalyDetector](#signalanomalydetector)
  - [SignalCoordinatedReads](#signalcoordinatedreads)
  - [SignalingHttpClient](#signalinghttpclient)
  - [SignalLogWatcher](#signallogwatcher)
  - [TelemetrySignalHandler](#telemetrysignalhandler)
  - [LongWindowDemo](#longwindowdemo)
  - [SignalReactionShowcase](#signalreactionshowcase)
- [Dependency Injection](#dependency-injection)

---

## Quick Start

```csharp
using Mostlylucid.Ephemeral;

// Long-lived work coordinator
await using var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

await coordinator.EnqueueAsync(new WorkItem("data"));

// One-shot parallel processing
await items.EphemeralForEachAsync(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });
```

---

## Core Coordinators

> **Package:** [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)

### EphemeralWorkCoordinator&lt;T&gt;

Long-lived work queue with bounded concurrency and observable window.

```csharp
await using var coordinator = new EphemeralWorkCoordinator<Request>(
    async (req, ct) => await HandleAsync(req, ct),
    new EphemeralOptions
    {
        MaxConcurrency = 8,
        MaxTrackedOperations = 200,
        MaxOperationLifetime = TimeSpan.FromMinutes(5)
    });

await coordinator.EnqueueAsync(request);

// Observe state
var running = coordinator.GetRunning();
var failed = coordinator.GetFailed();
var pending = coordinator.PendingCount;

// Graceful shutdown
coordinator.Complete();
await coordinator.DrainAsync();
```

### EphemeralKeyedWorkCoordinator&lt;TKey, T&gt;

Per-key sequential processing - items with same key processed in order.

```csharp
await using var coordinator = new EphemeralKeyedWorkCoordinator<Order, string>(
    order => order.CustomerId,  // Key selector
    async (order, ct) => await ProcessOrder(order, ct),
    new EphemeralOptions
    {
        MaxConcurrency = 16,      // Total parallel
        MaxConcurrencyPerKey = 1  // Sequential per customer
    });

await coordinator.EnqueueAsync(order);
```

### EphemeralResultCoordinator&lt;TInput, TResult&gt;

Capture results from async operations.

```csharp
await using var coordinator = new EphemeralResultCoordinator<Request, Response>(
    async (req, ct) => await FetchAsync(req, ct),
    new EphemeralOptions { MaxConcurrency = 4 });

var id = await coordinator.EnqueueAsync(request);
var snapshot = await coordinator.WaitForResult(id);
if (snapshot.HasResult)
    Console.WriteLine(snapshot.Result);
```

### PriorityWorkCoordinator&lt;T&gt;

Multiple priority lanes with configurable concurrency per lane.

```csharp
var coordinator = new PriorityWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new PriorityWorkCoordinatorOptions<WorkItem>(
        Lanes: new[] { new PriorityLane("high"), new PriorityLane("normal"), new PriorityLane("low") }
    ));

await coordinator.EnqueueAsync(item, "high");
```

---

## Configuration (EphemeralOptions)

```csharp
new EphemeralOptions
{
    // Concurrency
    MaxConcurrency = 8,                    // Max parallel operations
    MaxConcurrencyPerKey = 1,              // For keyed coordinators
    EnableDynamicConcurrency = false,      // Allow runtime adjustment

    // Memory
    MaxTrackedOperations = 200,            // Window size (LRU eviction)
    MaxOperationLifetime = TimeSpan.FromMinutes(5),

    // Fair scheduling (keyed only)
    EnableFairScheduling = false,          // Prevent hot key starvation
    FairSchedulingThreshold = 10,

    // Signals
    Signals = sharedSink,                  // Shared signal sink
    OnSignal = evt => { },                 // Sync callback
    OnSignalAsync = async (evt, ct) => { }, // Async callback
    CancelOnSignals = new HashSet<string> { "circuit-open" },
    DeferOnSignals = new HashSet<string> { "backpressure" },
    DeferCheckInterval = TimeSpan.FromMilliseconds(100),
    MaxDeferAttempts = 50,

    // Signal handler limits
    MaxConcurrentSignalHandlers = 4,
    MaxQueuedSignals = 1000
}
```

---

## Signals

Operations emit signals for cross-cutting observability.

```csharp
// Query signals
bool hasError = coordinator.HasSignal("error");
int count = coordinator.CountSignals("error");
var errors = coordinator.GetSignalsByPattern("error.*");

// Shared sink across coordinators
var sink = new SignalSink();
var c1 = new EphemeralWorkCoordinator<A>(body, new EphemeralOptions { Signals = sink });
var c2 = new EphemeralWorkCoordinator<B>(body, new EphemeralOptions { Signals = sink });
sink.Raise("system.busy");  // Both see it
```

---

## Atoms (Building Blocks)

### FixedWorkAtom

> **Package:** [mostlylucid.ephemeral.atoms.fixedwork](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.fixedwork)

Fixed worker pool with stats. Minimal API wrapper around EphemeralWorkCoordinator.

```csharp
using Mostlylucid.Ephemeral.Atoms.FixedWork;

await using var atom = new FixedWorkAtom<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    maxConcurrency: 4,
    maxTracked: 200);

await atom.EnqueueAsync(item);

// Get stats
var (pending, active, completed, failed) = atom.Stats();
Console.WriteLine($"Completed: {completed}, Failed: {failed}");

// Get recent operations
var snapshot = atom.Snapshot();

// Graceful shutdown
await atom.DrainAsync();
```

---

### KeyedSequentialAtom

> **Package:** [mostlylucid.ephemeral.atoms.keyedsequential](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.keyedsequential)

Per-key sequential processing with optional fair scheduling.

```csharp
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;

await using var atom = new KeyedSequentialAtom<Order, string>(
    keySelector: order => order.CustomerId,
    body: async (order, ct) => await ProcessOrder(order, ct),
    maxConcurrency: 16,
    perKeyConcurrency: 1,           // Sequential per key
    enableFairScheduling: true);    // Prevent hot key starvation

await atom.EnqueueAsync(order1);  // Customer A
await atom.EnqueueAsync(order2);  // Customer A - waits for order1
await atom.EnqueueAsync(order3);  // Customer B - parallel with A

var (pending, active, completed, failed) = atom.Stats();
await atom.DrainAsync();
```

---

### SignalAwareAtom

> **Package:** [mostlylucid.ephemeral.atoms.signalaware](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)

Pause or cancel intake based on ambient signals.

```csharp
using Mostlylucid.Ephemeral.Atoms.SignalAware;

var sink = new SignalSink();

await using var atom = new SignalAwareAtom<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    cancelOn: new HashSet<string> { "shutdown", "circuit-open" },
    deferOn: new HashSet<string> { "backpressure.*" },
    deferInterval: TimeSpan.FromMilliseconds(100),
    maxDeferAttempts: 50,
    signals: sink,
    maxConcurrency: 8);

// Enqueue work
await atom.EnqueueAsync(item);

// Raise ambient signals
atom.Raise("backpressure.downstream");  // New items defer
sink.Raise("shutdown");                  // New items rejected (returns -1)

await atom.DrainAsync();
```

---

### BatchingAtom

> **Package:** [mostlylucid.ephemeral.atoms.batching](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.batching)

Collect items into batches by size or time interval.

```csharp
using Mostlylucid.Ephemeral.Atoms.Batching;

await using var atom = new BatchingAtom<LogEntry>(
    onBatch: async (batch, ct) =>
    {
        Console.WriteLine($"Flushing {batch.Count} entries");
        await FlushToDatabase(batch, ct);
    },
    maxBatchSize: 100,
    flushInterval: TimeSpan.FromSeconds(5));

// Items are batched automatically
atom.Enqueue(new LogEntry("User logged in"));
atom.Enqueue(new LogEntry("Request received"));
// ... batch flushes when full OR after 5 seconds
```

---

### RetryAtom

> **Package:** [mostlylucid.ephemeral.atoms.retry](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry)

Exponential backoff retry wrapper.

```csharp
using Mostlylucid.Ephemeral.Atoms.Retry;

await using var atom = new RetryAtom<ApiRequest>(
    async (req, ct) => await CallExternalApi(req, ct),
    maxAttempts: 3,
    backoff: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
    maxConcurrency: 4);

// Automatically retries on failure with exponential backoff
// Attempt 1: immediate
// Attempt 2: 200ms delay
// Attempt 3: 400ms delay
await atom.EnqueueAsync(new ApiRequest("https://api.example.com"));

await atom.DrainAsync();
```

---

## Patterns (Ready-to-Use)

### SignalBasedCircuitBreaker

> **Package:** [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker)

Stateless circuit breaker using signal history window.

```csharp
using Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

var breaker = new SignalBasedCircuitBreaker(
    failureSignal: "api.failure",
    threshold: 5,
    windowSize: TimeSpan.FromSeconds(30));

// Check before making calls
if (breaker.IsOpen(coordinator))
{
    var retryAfter = breaker.GetTimeUntilClose(coordinator);
    throw new CircuitOpenException("Too many failures", retryAfter);
}

// Pattern matching variant
if (breaker.IsOpenMatching(coordinator, "error.*"))
    throw new CircuitOpenException("Error pattern detected");

// Get current failure count
int failures = breaker.GetFailureCount(coordinator);
```

---

### SignalDrivenBackpressure

> **Package:** [mostlylucid.ephemeral.patterns.backpressure](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure)

Queue depth management with automatic deferral on backpressure signals.

```csharp
using Mostlylucid.Ephemeral.Patterns.Backpressure;

var sink = new SignalSink();

await using var coordinator = SignalDrivenBackpressure.Create<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    maxConcurrency: 4);

// Enqueue work
await coordinator.EnqueueAsync(item);

// When downstream is slow
sink.Raise("backpressure.downstream");  // New work auto-defers

// When recovered
sink.Retract("backpressure.downstream"); // Work resumes
```

---

### ControlledFanOut

> **Package:** [mostlylucid.ephemeral.patterns.controlledfanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.controlledfanout)

Global + per-key gating for controlled parallelism.

```csharp
using Mostlylucid.Ephemeral.Patterns.ControlledFanOut;

await using var fanout = new ControlledFanOut<string, Request>(
    keySelector: req => req.TenantId,
    body: async (req, ct) => await ProcessAsync(req, ct),
    maxGlobalConcurrency: 100,  // Total parallel across all tenants
    perKeyConcurrency: 5);      // Max 5 parallel per tenant

// Items for same tenant processed with limit
await fanout.EnqueueAsync(requestA);  // Tenant1
await fanout.EnqueueAsync(requestB);  // Tenant1 - waits if 5 already running
await fanout.EnqueueAsync(requestC);  // Tenant2 - parallel with Tenant1

await fanout.DrainAsync();
```

---

### AdaptiveRateService

> **Package:** [mostlylucid.ephemeral.patterns.adaptiverate](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.adaptiverate)

Signal-driven rate limiting with automatic backoff.

```csharp
using Mostlylucid.Ephemeral.Patterns.AdaptiveRate;

await using var service = new AdaptiveRateService<ApiRequest>(
    async (req, ct) => await CallApiAsync(req, ct),
    maxConcurrency: 8);

// Process with automatic rate limit handling
await service.ProcessAsync(request);

// When API returns 429, emit signal with retry-after
// Signal: "rate-limit:500ms"
// Service auto-parses and delays

Console.WriteLine($"Pending: {service.PendingCount}, Active: {service.ActiveCount}");
```

---

### DynamicConcurrencyDemo

> **Package:** [mostlylucid.ephemeral.patterns.dynamicconcurrency](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.dynamicconcurrency)

Runtime concurrency scaling based on load signals.

```csharp
using Mostlylucid.Ephemeral.Patterns.DynamicConcurrency;

var sink = new SignalSink();

await using var demo = new DynamicConcurrencyDemo<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    minConcurrency: 2,
    maxConcurrency: 32,
    scaleUpPattern: "load.high",
    scaleDownPattern: "load.low");

await demo.EnqueueAsync(item);

// Concurrency adjusts automatically based on signals
sink.Raise("load.high");  // Concurrency doubles (up to max)
sink.Raise("load.low");   // Concurrency halves (down to min)

Console.WriteLine($"Current concurrency: {demo.CurrentMaxConcurrency}");

await demo.DrainAsync();
```

---

### KeyedPriorityFanOut

> **Package:** [mostlylucid.ephemeral.patterns.keyedpriorityfanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.keyedpriorityfanout)

Priority lanes with per-key ordering preserved.

```csharp
using Mostlylucid.Ephemeral.Patterns.KeyedPriorityFanOut;

await using var fanout = new KeyedPriorityFanOut<string, UserCommand>(
    keySelector: cmd => cmd.UserId,
    body: async (cmd, ct) => await HandleCommand(cmd, ct),
    maxConcurrency: 32,
    perKeyConcurrency: 1,  // Sequential per user
    maxPriorityDepth: 100);

// Normal lane
await fanout.EnqueueAsync(normalCommand);

// Priority lane - jumps the queue for that user
bool accepted = await fanout.EnqueuePriorityAsync(urgentCommand);

// Check lane depths
var counts = fanout.PendingCounts;
Console.WriteLine($"Priority: {counts.Priority}, Normal: {counts.Normal}");

await fanout.DrainAsync();
```

---

### ReactiveFanOutPipeline

> **Package:** [mostlylucid.ephemeral.patterns.reactivefanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.reactivefanout)

Two-stage pipeline with automatic backpressure.

```csharp
using Mostlylucid.Ephemeral.Patterns.ReactiveFanOut;

await using var pipeline = new ReactiveFanOutPipeline<WorkItem>(
    stage2Work: async (item, ct) => await SlowProcessing(item, ct),
    preStageWork: async (item, ct) => await FastPreprocessing(item, ct),
    stage1MaxConcurrency: 8,
    stage1MinConcurrency: 1,
    stage2MaxConcurrency: 4,
    backpressureThreshold: 32,  // Throttle when stage2 has 32+ pending
    reliefThreshold: 8);        // Resume when stage2 drops below 8

await pipeline.EnqueueAsync(item);

// Stage1 auto-throttles when stage2 backs up
Console.WriteLine($"Stage1 concurrency: {pipeline.Stage1CurrentMaxConcurrency}");
Console.WriteLine($"Stage2 pending: {pipeline.Stage2Pending}");

await pipeline.DrainAsync();
```

---

### SignalAnomalyDetector

> **Package:** [mostlylucid.ephemeral.patterns.anomalydetector](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.anomalydetector)

Moving-window anomaly detection.

```csharp
using Mostlylucid.Ephemeral.Patterns.AnomalyDetector;

var sink = new SignalSink();

var detector = new SignalAnomalyDetector(
    sink,
    pattern: "error.*",
    threshold: 5,
    window: TimeSpan.FromSeconds(10));

// Check for anomalies
if (detector.IsAnomalous())
{
    Console.WriteLine("Anomaly detected! Too many errors.");
    TriggerAlert();
}

// Get current match count
int errorCount = detector.GetMatchCount();
Console.WriteLine($"Errors in window: {errorCount}");
```

---

### SignalCoordinatedReads

> **Package:** [mostlylucid.ephemeral.patterns.signalcoordinatedreads](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalcoordinatedreads)

Quiesce reads during updates without hard locks.

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalCoordinatedReads;

// Run demo: readers pause when update signal is present
var result = await SignalCoordinatedReads.RunAsync(
    readCount: 10,
    updateCount: 1);

Console.WriteLine($"Reads: {result.ReadsCompleted}, Updates: {result.UpdatesCompleted}");
Console.WriteLine($"Signals: {string.Join(", ", result.Signals)}");

// Manual implementation:
var sink = new SignalSink();

await using var readers = new EphemeralWorkCoordinator<Query>(
    body,
    new EphemeralOptions
    {
        DeferOnSignals = new HashSet<string> { "update.in-progress" },
        Signals = sink
    });

// Readers auto-defer when update is running
sink.Raise("update.in-progress");  // Readers wait
sink.Raise("update.done");         // Readers resume
```

---

### SignalingHttpClient

> **Package:** [mostlylucid.ephemeral.patterns.signalinghttp](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalinghttp)

HTTP client with progress signals.

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalingHttp;

var httpClient = new HttpClient();
var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/large-file");

// Create an emitter from your coordinator
// (emitter is any ISignalEmitter - operations implement this)

byte[] data = await SignalingHttpClient.DownloadWithSignalsAsync(
    httpClient,
    request,
    emitter);

// Signals emitted during download:
// - stage.starting
// - progress:0
// - stage.request
// - stage.headers
// - stage.reading
// - progress:25, progress:50, progress:75, progress:100
// - stage.completed
```

---

### SignalLogWatcher

> **Package:** [mostlylucid.ephemeral.patterns.signallogwatcher](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher)

Watch signal window for patterns and trigger callbacks.

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalLogWatcher;

var sink = new SignalSink();

await using var watcher = new SignalLogWatcher(
    sink,
    onMatch: evt =>
    {
        Console.WriteLine($"Error detected: {evt.Signal} at {evt.Timestamp}");
        AlertOps(evt);
    },
    pattern: "error.*",
    pollInterval: TimeSpan.FromMilliseconds(200));

// Watcher runs in background, calling onMatch for each new error signal
sink.Raise("error.database");    // -> onMatch called
sink.Raise("error.timeout");     // -> onMatch called
sink.Raise("info.started");      // -> ignored (doesn't match pattern)
```

---

### TelemetrySignalHandler

> **Package:** [mostlylucid.ephemeral.patterns.telemetry](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.telemetry)

OpenTelemetry/Application Insights integration.

```csharp
using Mostlylucid.Ephemeral.Patterns.Telemetry;

// Use in-memory for testing, or implement ITelemetryClient for real telemetry
var telemetry = new InMemoryTelemetryClient();

await using var handler = new TelemetrySignalHandler(telemetry);

// Wire up to coordinator
var options = new EphemeralOptions
{
    OnSignal = signal => handler.OnSignal(signal)
};

// Signals are processed asynchronously
// - "error.*" signals -> TrackExceptionAsync
// - "perf.*" signals -> TrackMetricAsync
// - all signals -> TrackEventAsync

Console.WriteLine($"Queued: {handler.QueuedCount}");
Console.WriteLine($"Processed: {handler.ProcessedCount}");
Console.WriteLine($"Dropped: {handler.DroppedCount}");

// Check recorded events
var events = telemetry.GetEvents();
```

---

### LongWindowDemo

> **Package:** [mostlylucid.ephemeral.patterns.longwindowdemo](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.longwindowdemo)

Demonstrates large window configuration for audit trails.

```csharp
using Mostlylucid.Ephemeral.Patterns.LongWindowDemo;

// Configure coordinator with large tracking window
var options = new EphemeralOptions
{
    MaxTrackedOperations = 10000,
    MaxOperationLifetime = TimeSpan.FromHours(24)
};
```

---

### SignalReactionShowcase

> **Package:** [mostlylucid.ephemeral.patterns.signalreactionshowcase](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalreactionshowcase)

Demonstrates signal dispatch patterns and callbacks.

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalReactionShowcase;

// See source for signal dispatch examples
// Demonstrates OnSignal, OnSignalAsync, CancelOnSignals, DeferOnSignals
```

---

## Dependency Injection

```csharp
// Register in Startup/Program.cs
services.AddEphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

// Named coordinators
services.AddEphemeralWorkCoordinator<WorkItem>("priority",
    async (item, ct) => await ProcessPriorityAsync(item, ct));

// Inject and use
public class MyService(IEphemeralCoordinatorFactory<WorkItem> factory)
{
    public async Task DoWork()
    {
        var coordinator = factory.CreateCoordinator();
        await coordinator.EnqueueAsync(new WorkItem());
    }
}
```

---

## Target Frameworks

- .NET 6.0, 7.0, 8.0, 9.0, 10.0

## License

MIT
