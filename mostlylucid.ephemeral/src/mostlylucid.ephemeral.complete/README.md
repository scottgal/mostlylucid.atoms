# Mostlylucid.Ephemeral.Complete

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.complete.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)

**All of Mostlylucid.Ephemeral in a single DLL** - bounded async execution with signal-based coordination.

```bash
dotnet add package mostlylucid.ephemeral.complete
```

This package compiles all core, atom, and pattern code into one assembly. For individual packages, see the links in each
section below.

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
    - [Data Storage Atoms](#data-storage-atoms)
    - [MoleculeRunner & AtomTrigger](#moleculerunner--atomtrigger)
    - [SlidingCacheAtom](#slidingcacheatom)
    - [EphemeralLruCache](#ephemerallrucache)
    - [Echo Maker](#echo-maker)
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
    - [PersistentSignalWindow](#persistentsignalwindow)
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

### Service registration

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8, MaxTrackedOperations = 128 });

builder.Services.AddEphemeralSignalJobRunner<LogWatcherJobs>();

var app = builder.Build();
app.MapPost("/", async ([FromServices] IEphemeralCoordinatorFactory<WorkItem> factory, WorkItem item) =>
{
    var coordinator = factory.CreateCoordinator();
    await coordinator.EnqueueAsync(item);
    return Results.Accepted();
});

await app.RunAsync();
```

The familiar `services.AddCoordinator<T>()` helpers and `AddEphemeralSignalJobRunner<T>()` keep service registration concise, let DI own the sink/runner, and make the new responsibility/cache/logging stories a single click away.

### Attribute-driven jobs

`mostlylucid.ephemeral.complete` bundles `mostlylucid.ephemeral.attributes`, so attribute pipelines are part of the core
surface. Treat the runner as a first-class signal consumer: decorated methods join the same caching, logging, and
pinning stories, and each attribute can declare `Priority`, job-level `MaxConcurrency`, `Lane`, `Key` sources, signal
emissions, pin/expire overrides, and retries.

Key attribute knobs:

- **Ordering & lanes**: Use `Priority`, `MaxConcurrency`, and `Lane` to keep work in deterministic order while hot paths
  stay separate.
- **Keying & tagging**: `OperationKey`, `KeyFromSignal`, `KeyFromPayload`, and `[KeySource]` help you group work with
  meaningful keys for logging, fair scheduling, and diagnostics.
- **Pinning & retries**: `Pin`, `ExpireAfterMs`, `AwaitSignals`, `MaxRetries`, and `RetryDelayMs` let handlers extend
  their visibility, gate execution until dependencies arrive, and heal with retries while emitting failure signals.
- **Signal choreography**: Emit `EmitOnStart`, `EmitOnComplete`, and `EmitOnFailure` to signal downstream stages, log
  watchers, or other coordinators without manual wiring.

```csharp
var sink = new SignalSink();
await using var runner = new EphemeralSignalJobRunner(sink, new[] { new LogWatcherJobs(sink) });

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddProvider(new SignalLoggerProvider(new TypedSignalSink<SignalLogPayload>(sink)));
});

var logger = loggerFactory.CreateLogger("orders");
logger.LogError(new EventId(1001, "DbFailure"), "Order store failed");

// Later tasks or other services can also raise watcher-friendly signals directly:
sink.Raise("log.error.orders.dbfailure", key: "orders");
```

```csharp
public sealed class LogWatcherJobs
{
    private readonly SignalSink _sink;

    public LogWatcherJobs(SignalSink sink) => _sink = sink;

    [EphemeralJob("log.error.*", Priority = 1, MaxConcurrency = 2, Lane = "hot:4", EmitOnComplete = new[] { "incident.created" })]
    public Task EscalateAsync(SignalEvent signal)
    {
        Console.WriteLine($"escalating {signal.Signal} for {signal.Key}");
        _sink.Raise("incident.created", key: signal.Key);
        return Task.CompletedTask;
    }

    [EphemeralJob("incident.created", EmitOnStart = new[] { "incident.monitor.start" })]
    public Task NotifyAsync(SignalEvent signal)
    {
        Console.WriteLine($"notified incident for {signal.Key}");
        return Task.CompletedTask;
    }
}
```

This runner now sits at startup and reacts whenever `log.error.*` or any emitted signal hits the sink. Attribute
handlers can also read keys from signals/payloads, pin work until downstream acks, emit completion/failure signals, and
slot into lanes for ordering. For DI-first setups use `services.AddEphemeralSignalJobRunner<T>()` (or the scoped
variant) so the runner and sink are managed by the container.

[EphemeralJobs(SignalPrefix = "stage", DefaultLane = "pipeline")]
public sealed class StageJobs
{
[EphemeralJob("ingest", EmitOnComplete = new[] { "stage.ingest.done" })]
public Task IngestAsync(SignalEvent evt) => Console.Out.WriteLineAsync(evt.Signal);

    [EphemeralJob("finalize")]
    public Task FinalizeAsync(SignalEvent evt) => Console.Out.WriteLineAsync("final stage");

}

var stageSink = new SignalSink();
await using var stageRunner = new EphemeralSignalJobRunner(stageSink, new[] { new StageJobs() });
stageSink.Raise("stage.ingest");

Pin-heavy jobs can rely on `ResponsibilitySignalManager.PinUntilQueried` (default ack pattern `responsibility.ack.*`) to
keep their operations visible until a downstream reader fetches the payload, while `OperationEchoMaker`/
`OperationEchoAtom` persist the final signal stream so auditors or molecules can still “taste” the last state even after
the atom dies.

### Scheduled tasks

`mostlylucid.ephemeral.complete` also contains `mostlylucid.ephemeral.atoms.scheduledtasks`. Define cron or JSON
schedules via `ScheduledTaskDefinition` (cron, signal, optional `key`, `payload`, `description`, `timeZone`, `format`,
`runOnStartup`, etc.), and let `ScheduledTasksAtom` enqueue durable work through `DurableTaskAtom`. Each scheduled job
raises the configured signal inside a coordinator window, so it inherits pinning, logging, and responsibility semantics
while your molecules or attribute pipelines respond to the emitted signal wave.

Every `DurableTask` carries the schedule `Name`, `Signal`, optional `Key`, even a typed `Payload`, and `Description`, so downstream listeners immediately know which job ran and what metadata (filenames, URLs, etc.) to consume. Call `DurableTaskAtom.WaitForIdleAsync()` when you just want to wait for the current burst of scheduled work to finish without completing the atom, keeping the scheduler ready for the next cron tick.

### Logging & Signals

`mostlylucid.ephemeral.logging` mirrors Microsoft.Extensions.Logging into signals and vice versa. Start by attaching
`SignalLoggerProvider` to your logger factory so log events raise `log.*` signals, and hook `SignalToLoggerAdapter` if
you want signals to flow back into the standard log pipeline.

```csharp
var sink = new SignalSink();
var typedSink = new TypedSignalSink<SignalLogPayload>(sink);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddProvider(new SignalLoggerProvider(typedSink));
});

using var watcher = new EphemeralSignalJobRunner(sink, new[] { new LogWatcherJobs(sink) });

var logger = loggerFactory.CreateLogger("orders");
logger.LogError(new EventId(1001, "DbFailure"), "Order store failed");
```

```csharp
public sealed class LogWatcherJobs
{
    private readonly SignalSink _sink;

    public LogWatcherJobs(SignalSink sink) => _sink = sink;

    [EphemeralJob("log.error.*")]
    public Task EscalateAsync(SignalEvent signal)
    {
        _sink.Raise("incident.created", key: signal.Key);
        return Task.CompletedTask;
    }

    [EphemeralJob("incident.created")]
    public Task NotifyAsync(SignalEvent signal)
    {
        Console.WriteLine($"Incident for {signal.Key}");
        return Task.CompletedTask;
    }
}
```

Use `SignalToLoggerAdapter` to mirror the resulting signals back into standard logs so your monitoring stack sees both
sides of the bridge.

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

## Responsibility Signals & Finalization

Need to keep results visible just long enough for downstream consumers? `ResponsibilitySignalManager` lets you pin an
operation until an ack signal arrives (default pattern `responsibility.ack.*` with key=`operationId`). Provide an
optional `description` so the operation can describe its responsibility, and set `maxPinDuration` to gracefully
self-clear if the consumer never shows up.

```csharp
var manager = new ResponsibilitySignalManager(coordinator, sink, maxPinDuration: TimeSpan.FromMinutes(5));
if (manager.PinUntilQueried(operationId, "file.ready", ackKey: fileId, description: "Awaiting fetch"))
{
    sink.Raise("file.ready", key: fileId);
}
// Consumer acknowledges the work
sink.Raise("file.ready.ack", key: fileId);
```

```csharp
using Mostlylucid.Ephemeral.Patterns;

var notes = new LastWordsNoteAtom(async note => await noteRepository.SaveAsync(note));
coordinator.OperationFinalized += snapshot =>
{
    var note = new LastWordsNote(
        OperationId: snapshot.OperationId,
        Key: snapshot.Key,
        Signal: snapshot.Signals?.FirstOrDefault(),
        Timestamp: DateTimeOffset.UtcNow);

    _ = notes.EnqueueAsync(note);
};
```

`LastWordsNote` stays tiny (operation id, key, signal, timestamp), so you can record whatever minimal state you care
about before the operation is collected.

The coordinator also keeps a short-lived echo of the final signals (enabled via `EnableOperationEcho`) that you can
inspect with `GetEchoes()` when you need to replay the trimmed signal wave without keeping the full operation around.

```csharp
var recentErrors = coordinator.GetEchoes(pattern: "error.*")
    .Where(e => e.Timestamp > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))
    .ToList();

if (recentErrors.Any())
    logger.LogWarning("Trimmed errors: {Count}", recentErrors.Count);
```

`OperationEchoRetention` and `OperationEchoCapacity` let you balance how many echoes you keep and how long they linger,
so you can replay the “last words” just long enough to surface diagnostics.

The manager automatically unpins when the ack fires, but you can call `CompleteResponsibility(operationId)` to end the
responsibility early (e.g., on retries). Operations still raise `OperationFinalized` when the window trims them, so
subscribe if you want to emit a final signal, log diagnostics, or run “last words” cleanup.

---

## Atoms (Building Blocks)

### FixedWorkAtom

> **Package:
** [mostlylucid.ephemeral.atoms.fixedwork](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.fixedwork)

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

> **Package:
** [mostlylucid.ephemeral.atoms.keyedsequential](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.keyedsequential)

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

> **Package:
** [mostlylucid.ephemeral.atoms.signalaware](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)

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

> **Package:
** [mostlylucid.ephemeral.atoms.batching](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.batching)

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

### Data Storage Atoms

> **Package:** [mostlylucid.ephemeral.atoms.data](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.data)

Shared configuration for storage atoms (`DataStorageConfig`, `IDataStorageAtom<TKey, TValue>`) plus the signal conventions that drive file, SQLite, and PostgreSQL adapters.

```csharp
using Mostlylucid.Ephemeral.Atoms.Data;
using Mostlylucid.Ephemeral.Atoms.Data.File;

var sink = new SignalSink();
var config = new DataStorageConfig
{
    DatabaseName = "orders",
    SignalPrefix = "save.data",
    LoadSignalPrefix = "load.data",
    DeleteSignalPrefix = "delete.data",
    MaxConcurrency = 1
};

await using var storage = new FileDataStorageAtom<string, Order>(sink, config, "./orders");

storage.EnqueueSave("order-123", new Order { Id = "order-123", Total = 42.00m });
var loaded = await storage.LoadAsync("order-123");
```

Use the same `DataStorageConfig` with [`Mostlylucid.Ephemeral.Atoms.Data.Sqlite`](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.data.sqlite) or [`Mostlylucid.Ephemeral.Atoms.Data.Postgres`](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.data.postgres) implementations for durable, signal-driven persistence powered by SQLite/Postgres. Attribute jobs can subscribe to `saved.data.{dbname}` signals to kick off downstream work while `load.data.{dbname}` triggers hydrate caches.


---

### MoleculeRunner & AtomTrigger

> **Package:
** [mostlylucid.ephemeral.atoms.molecules](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.molecules)

Blueprints composed with `MoleculeBlueprintBuilder` let you define the atoms (payment, inventory, shipping,
notification) that should run when a signal such as `order.placed` arrives. `MoleculeRunner` listens for the trigger
pattern, creates a shared `MoleculeContext`, and executes each step while you subscribe to start/completion events. Use
`AtomTrigger` when one atom's signal should start another coordinator or molecule.

```csharp
var sink = new SignalSink();
var blueprint = new MoleculeBlueprintBuilder("order", "order.placed")
    .AddAtom(async (ctx, ct) => await paymentCoordinator.EnqueueAsync(ctx.TriggerSignal.Key!, ct))
    .AddAtom(async (ctx, ct) =>
    {
        ctx.Raise("order.payment.complete", ctx.TriggerSignal.Key);
        await inventoryCoordinator.EnqueueAsync(ctx.TriggerSignal.Key!, ct);
    })
    .Build();

await using var runner = new MoleculeRunner(sink, new[] { blueprint }, serviceProvider);
using var trigger = new AtomTrigger(sink, "order.payment.complete", async (signal, ct) =>
{
    await notificationCoordinator.EnqueueAsync(signal.Key!, ct);
});

sink.Raise("order.placed", key: "order-42");
```

Molecule steps can raise additional signals (`ctx.Raise("order.shipping.start")`) so the rest of the system picks up the
baton.

---

### SlidingCacheAtom

> **Package:
** [mostlylucid.ephemeral.atoms.slidingcache](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.slidingcache)

Cache with sliding expiration - accessing a result resets its TTL.

```csharp
using Mostlylucid.Ephemeral.Atoms.SlidingCache;

await using var cache = new SlidingCacheAtom<string, UserProfile>(
    async (userId, ct) => await LoadUserProfileAsync(userId, ct),
    slidingExpiration: TimeSpan.FromMinutes(5),
    absoluteExpiration: TimeSpan.FromHours(1),
    maxSize: 1000);

// First call: computes and caches
var profile = await cache.GetOrComputeAsync("user-123");

// Second call within 5 minutes: returns cached, resets TTL
var cached = await cache.GetOrComputeAsync("user-123");

// Try get without computation (still resets TTL on hit)
if (cache.TryGet("user-123", out var profile))
    Console.WriteLine(profile.Name);

// Get stats
var stats = cache.GetStats();
Console.WriteLine($"Entries: {stats.TotalEntries}, Hot: {stats.HotEntries}");
```

### EphemeralLruCache

> **Package:** core (`mostlylucid.ephemeral`) — self-optimizing cache with sliding TTL on every hit and extended TTL for
> hot keys.

```csharp
using Mostlylucid.Ephemeral;

var cache = new EphemeralLruCache<string, Widget>(new EphemeralLruCacheOptions
{
    DefaultTtl = TimeSpan.FromMinutes(5),
    HotKeyExtension = TimeSpan.FromMinutes(30),
    HotAccessThreshold = 3,
    MaxSize = 10_000,
    SampleRate = 5 // emit 1 in 5 signals
});

var widget = await cache.GetOrAddAsync("widget:42", async key =>
{
    var data = await LoadWidgetAsync(key);
    return data!;
});

// Stats and signals to see how the cache self-focuses on hot keys
var stats = cache.GetStats();              // hot/expired counts, size
var signals = cache.GetSignals("cache.*"); // cache.hot/evict/miss/hit
```

> Tip: `MemoryCache` can be configured for sliding expiration, but it never emits the hot/cold signals or extends TTL
> for hot keys. `EphemeralLruCache` is the self-optimizing default in the core package (and in `SqliteSingleWriter`)
> whenever you want the cache to focus on the active working set.

### Echo Maker

> **Package:** [mostlylucid.ephemeral.atoms.echo](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.echo)

Capture the typed “last words” that an operation emits before it is trimmed. The atom keeps a bounded window of signal
payloads (matching `ActivationSignalPattern` / `CaptureSignalPattern`) and when `OperationFinalized` fires it produces
`OperationEchoEntry<TPayload>` records you can persist via `OperationEchoAtom<TPayload>`.

```csharp
var sink = new SignalSink();
var typedSink = new TypedSignalSink<EchoPayload>(sink);
var echoAtom = new OperationEchoAtom<EchoPayload>(async echo => await repository.AppendAsync(echo));

await using var coordinator = new EphemeralWorkCoordinator<JobItem>(ProcessAsync);
using var maker = coordinator.EnableOperationEchoing(
    typedSink,
    echoAtom,
    new OperationEchoMakerOptions<EchoPayload>
    {
        ActivationSignalPattern = "echo.capture",
        CaptureSignalPattern = "echo.*",
        MaxTrackedOperations = 128
    });

typedSink.Raise("echo.capture", new EchoPayload("order-1", "archived"), key: "order-1");
```

Attribute jobs just raise the typed signal with whatever state they deem critical, and the maker keeps the working set
bounded while you persist the echo.

---

## Patterns (Ready-to-Use)

### SignalBasedCircuitBreaker

> **Package:
** [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker)

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

> **Package:
** [mostlylucid.ephemeral.patterns.backpressure](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure)

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

> **Package:
** [mostlylucid.ephemeral.patterns.controlledfanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.controlledfanout)

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

> **Package:
** [mostlylucid.ephemeral.patterns.adaptiverate](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.adaptiverate)

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

> **Package:
** [mostlylucid.ephemeral.patterns.dynamicconcurrency](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.dynamicconcurrency)

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

> **Package:
** [mostlylucid.ephemeral.patterns.keyedpriorityfanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.keyedpriorityfanout)

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

> **Package:
** [mostlylucid.ephemeral.patterns.reactivefanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.reactivefanout)

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

> **Package:
** [mostlylucid.ephemeral.patterns.anomalydetector](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.anomalydetector)

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

> **Package:
** [mostlylucid.ephemeral.patterns.signalcoordinatedreads](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalcoordinatedreads)

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

> **Package:
** [mostlylucid.ephemeral.patterns.signalinghttp](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalinghttp)

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

> **Package:
** [mostlylucid.ephemeral.patterns.signallogwatcher](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher)

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

> **Package:
** [mostlylucid.ephemeral.patterns.telemetry](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.telemetry)

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

> **Package:
** [mostlylucid.ephemeral.patterns.longwindowdemo](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.longwindowdemo)

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

> **Package:
** [mostlylucid.ephemeral.patterns.signalreactionshowcase](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalreactionshowcase)

Demonstrates signal dispatch patterns and callbacks.

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalReactionShowcase;

// See source for signal dispatch examples
// Demonstrates OnSignal, OnSignalAsync, CancelOnSignals, DeferOnSignals
```

---

### PersistentSignalWindow

> **Package:
** [mostlylucid.ephemeral.patterns.persistentwindow](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.persistentwindow)

Signal window with SQLite persistence - survives process restarts.

```csharp
using Mostlylucid.Ephemeral.Patterns.PersistentWindow;

await using var window = new PersistentSignalWindow(
    "Data Source=signals.db",
    flushInterval: TimeSpan.FromSeconds(30));

// On startup: restore previous signals
await window.LoadFromDiskAsync(maxAge: TimeSpan.FromHours(24));

// Raise signals as normal
window.Raise("order.completed", key: "order-service");
window.Raise("payment.processed", key: "payment-service");

// Query signals
var recentOrders = window.Sense("order.*");

// Signals automatically flush every 30 seconds
// Also flushes on dispose

// Get stats
var stats = window.GetStats();
Console.WriteLine($"In memory: {stats.InMemoryCount}, Flushed: {stats.LastFlushedId}");
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

Modern DI roots may prefer the shorter helpers such as `services.AddCoordinator<T>(...)`,
`services.AddScopedCoordinator<T>(...)`, or `services.AddKeyedCoordinator<T, TKey>(...)` since they read like normal
`AddX` registrations; they simply delegate to the Ephemeral-specific helpers under the hood.

---

## Target Frameworks

- .NET 6.0, 7.0, 8.0, 9.0, 10.0

## License

Unlicense (public domain)
