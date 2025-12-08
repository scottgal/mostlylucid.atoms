# Mostlylucid.Ephemeral

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral)
[![License](https://img.shields.io/badge/license-Unlicense-blue.svg)](../../UNLICENSE)

**Fire... and Don't *Quite* Forget.**

Bounded, observable, self-cleaning async execution with signal-based coordination.

```bash
dotnet add package mostlylucid.ephemeral
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral;

// One-shot parallel processing
await items.EphemeralForEachAsync(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

// Long-lived coordinator
await using var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

await coordinator.EnqueueAsync(new WorkItem("data"));

// See what's happening
var running = coordinator.GetRunning();
var failed = coordinator.GetFailed();
var pending = coordinator.PendingCount;

// Graceful shutdown
coordinator.Complete();
await coordinator.DrainAsync();
```

---

## All Options

```csharp
new EphemeralOptions
{
    // ═══════════════════════════════════════════════════════════════
    // CONCURRENCY
    // ═══════════════════════════════════════════════════════════════

    // Max parallel operations overall
    // Default: Environment.ProcessorCount
    MaxConcurrency = 8,

    // Allow runtime concurrency adjustment via SetMaxConcurrency()
    // Default: false (fastest hot-path)
    EnableDynamicConcurrency = false,

    // Max parallel operations per key (keyed coordinators only)
    // Default: 1 (sequential per key)
    MaxConcurrencyPerKey = 1,

    // ═══════════════════════════════════════════════════════════════
    // MEMORY / WINDOW
    // ═══════════════════════════════════════════════════════════════

    // Max operations retained in memory (LRU eviction)
    // Default: 200
    MaxTrackedOperations = 200,

    // Max age for tracked operations before cleanup
    // Default: 5 minutes
    MaxOperationLifetime = TimeSpan.FromMinutes(5),

    // ═══════════════════════════════════════════════════════════════
    // FAIR SCHEDULING (keyed coordinators only)
    // ═══════════════════════════════════════════════════════════════

    // Prevent hot keys from starving cold keys
    // Default: false (FIFO ordering)
    EnableFairScheduling = false,

    // Pending count before a key is deprioritized
    // Default: 10
    FairSchedulingThreshold = 10,

    // ═══════════════════════════════════════════════════════════════
    // SIGNALS
    // ═══════════════════════════════════════════════════════════════

    // Shared signal sink across coordinators
    // Default: null (isolated)
    Signals = new SignalSink(),

    // Sync callback on signal raise (keep fast!)
    // Default: null
    OnSignal = evt => Console.WriteLine($"Signal: {evt.Signal}"),

    // Async callback on signal raise (background queue, non-blocking)
    // Default: null
    OnSignalAsync = async (evt, ct) => await LogToService(evt, ct),

    // Sync callback on signal retract
    // Default: null
    OnSignalRetracted = evt => Console.WriteLine($"Retracted: {evt.Signal}"),

    // Async callback on signal retract
    // Default: null
    OnSignalRetractedAsync = async (evt, ct) => await NotifyService(evt, ct),

    // Max concurrent async signal handlers
    // Default: 4
    MaxConcurrentSignalHandlers = 4,

    // Max queued signals before dropping oldest
    // Default: 1000
    MaxQueuedSignals = 1000,

    // Self-documenting: signals this coordinator may emit
    // Default: null (no enforcement)
    Emits = new[] { "started", "completed", "error" },

    // Self-documenting: signals this coordinator listens for
    // Default: null (no enforcement)
    Listens = new[] { "backpressure", "shutdown" },

    // Constraints for signal propagation (cycles, depth, terminals)
    // Default: null
    SignalConstraints = new SignalConstraints { MaxDepth = 10 },

    // ═══════════════════════════════════════════════════════════════
    // SIGNAL-BASED CONTROL FLOW
    // ═══════════════════════════════════════════════════════════════

    // Skip new items when these signals are present
    // Use for circuit-breaker patterns
    // Default: null
    CancelOnSignals = new HashSet<string> { "circuit-open", "shutdown" },

    // Delay new items when these signals are present
    // Use for backpressure patterns
    // Default: null
    DeferOnSignals = new HashSet<string> { "backpressure", "rate-limited" },

    // How often to recheck when deferring
    // Default: 100ms
    DeferCheckInterval = TimeSpan.FromMilliseconds(100),

    // Max defer attempts before running anyway
    // Default: 50 (5 seconds at 100ms)
    MaxDeferAttempts = 50,

    // ═══════════════════════════════════════════════════════════════
    // SAMPLING
    // ═══════════════════════════════════════════════════════════════

    // Callback after each operation with window snapshot
    // Runs on caller's thread - keep it cheap!
    // Default: null
    OnSample = snapshot => metrics.Record(snapshot.Count)
}
```

---

## Coordinators

### EphemeralWorkCoordinator&lt;T&gt;

Long-lived work queue with bounded concurrency.

```csharp
await using var coordinator = new EphemeralWorkCoordinator<Request>(
    async (req, ct) => await HandleAsync(req, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

// Enqueue work
await coordinator.EnqueueAsync(request);
var id = await coordinator.EnqueueWithIdAsync(request);  // Get operation ID

// Query state
var running = coordinator.GetRunning();
var completed = coordinator.GetCompleted();
var failed = coordinator.GetFailed();
var snapshot = coordinator.GetSnapshot();
var byId = coordinator.GetById(id);

// Stats
int pending = coordinator.PendingCount;
int active = coordinator.ActiveCount;
int totalCompleted = coordinator.TotalCompleted;
int totalFailed = coordinator.TotalFailed;

// Dynamic concurrency (requires EnableDynamicConcurrency = true)
coordinator.SetMaxConcurrency(16);
int current = coordinator.CurrentMaxConcurrency;

// Signals
bool hasError = coordinator.HasSignal("error");
int errorCount = coordinator.CountSignals("error");
var errors = coordinator.GetSignalsByPattern("error.*");
var recent = coordinator.GetSignalsSince(DateTimeOffset.UtcNow.AddMinutes(-1));

// Shutdown
coordinator.Complete();          // Stop accepting new work
await coordinator.DrainAsync();  // Wait for in-flight to finish
```

### EphemeralKeyedWorkCoordinator&lt;T, TKey&gt;

Per-key sequential processing with optional fair scheduling.

```csharp
await using var coordinator = new EphemeralKeyedWorkCoordinator<Order, string>(
    keySelector: order => order.CustomerId,
    body: async (order, ct) => await ProcessOrder(order, ct),
    new EphemeralOptions
    {
        MaxConcurrency = 16,          // Total parallel
        MaxConcurrencyPerKey = 1,     // Sequential per customer
        EnableFairScheduling = true   // Prevent hot customer starvation
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
else if (snapshot.Exception != null)
    Console.WriteLine($"Failed: {snapshot.Exception.Message}");
```

### PriorityWorkCoordinator&lt;T&gt;

Multiple priority lanes.

```csharp
var coordinator = new PriorityWorkCoordinator<WorkItem>(
    body: async (item, ct) => await ProcessAsync(item, ct),
    new PriorityWorkCoordinatorOptions<WorkItem>(
        Lanes: new[]
        {
            new PriorityLane("critical", MaxDepth: 100),
            new PriorityLane("high"),
            new PriorityLane("normal"),
            new PriorityLane("low")
        }
    ));

await coordinator.EnqueueAsync(item, "critical");
```

### Extension Method

One-shot parallel processing of collections.

```csharp
await items.EphemeralForEachAsync(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions
    {
        MaxConcurrency = 8,
        OnSignal = evt => Console.WriteLine(evt.Signal)
    });
```

---

## Signals

### Emitting Signals

```csharp
// From within operation body (via ISignalEmitter passed to body)
await coordinator.EnqueueAsync(request);  // Body receives emitter

// From shared sink
var sink = new SignalSink();
sink.Raise("backpressure.downstream");
sink.Raise(new SignalEvent("error.timeout", operationId, key, DateTimeOffset.UtcNow));

// Retract signals
sink.Retract("backpressure.downstream");
```

### Querying Signals

```csharp
// Exact match
bool hasError = coordinator.HasSignal("error");
int count = coordinator.CountSignals("error");

// Pattern matching (glob-style: * and ?)
var errors = coordinator.GetSignalsByPattern("error.*");
var httpErrors = coordinator.GetSignalsByPattern("http.error.*");

// Time-based
var recent = coordinator.GetSignalsSince(DateTimeOffset.UtcNow.AddSeconds(-30));

// From shared sink
var snapshot = sink.Sense();
var filtered = sink.Sense(s => s.Signal.StartsWith("error"));
```

### Shared Signal Sink

```csharp
var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(5));

var coordinator1 = new EphemeralWorkCoordinator<A>(body, new EphemeralOptions { Signals = sink });
var coordinator2 = new EphemeralWorkCoordinator<B>(body, new EphemeralOptions { Signals = sink });

// Both coordinators see signals raised by either
sink.Raise("system.maintenance");
```

---

## Molecules & Atom triggers

Use the `mostlylucid.ephemeral.atoms.molecules` package when you want to treat several atoms as one workflow.
`MoleculeBlueprintBuilder` lets you describe each step, wire its signals into downstream steps, and build a blueprint
that `MoleculeRunner` listens for (matching on a trigger signal and shared `SignalSink`). The runner raises
`MoleculeStarted`, `MoleculeCompleted`, and `MoleculeFailed`, so you can observe what the chef (coordinator) dropped
into the soup, and `AtomTrigger` gives you a lightweight watcher that starts additional atoms or molecules whenever a
signal pattern fires.

## Scheduled tasks

`DurableTaskAtom` + `ScheduledTasksAtom` turn cron/JSON schedules into durable work inside your coordinator window.
Create `ScheduledTaskDefinition`s (cron expression, signal, optional `key`, `payload`, `description`, `timeZone`,
`format`, `runOnStartup`, etc.), point the scheduler at `SignalSink`, and let it enqueue `DurableTask`s that emit the
configured signals. Because the work runs inside the same coordinator, it inherits pinning, signal logging, and
responsibility semantics along with your ad-hoc work.

## Responsibility signals & echoes

Coordinators implement `IOperationPinning` and raise `OperationFinalized` when entries leave the window.
`ResponsibilitySignalManager` provides `PinUntilQueried` so atoms can declare responsibility (pin) until a downstream
ack signal arrives, preventing resources from disappearing while they await consumers. Subscribe to `OperationFinalized`
to record “last words” (logs, diagnostics, signals) via `LastWordsNoteAtom`, or let `OperationEchoMaker` capture typed
echoes for a configurable window so molecules, monitors, or auditors can still read the final signal wave.

## Dependency Injection

```csharp
// Register
services.AddEphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

// Named coordinators
services.AddEphemeralWorkCoordinator<WorkItem>("priority",
    async (item, ct) => await ProcessPriorityAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 4 });

// Inject
public class MyService(IEphemeralCoordinatorFactory<WorkItem> factory)
{
    public async Task DoWork()
    {
        var coordinator = factory.CreateCoordinator();
        // or: factory.CreateCoordinator("priority")
        await coordinator.EnqueueAsync(new WorkItem());
    }
}
```

---

## Related Packages

| Package                                                                                                                       | Description                   |
|-------------------------------------------------------------------------------------------------------------------------------|-------------------------------|
| [mostlylucid.ephemeral.atoms.fixedwork](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.fixedwork)                 | Fixed worker pool with stats  |
| [mostlylucid.ephemeral.atoms.keyedsequential](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.keyedsequential)     | Per-key sequential processing |
| [mostlylucid.ephemeral.atoms.signalaware](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)             | Pause/cancel on signals       |
| [mostlylucid.ephemeral.atoms.batching](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.batching)                   | Time/size batching            |
| [mostlylucid.ephemeral.atoms.retry](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry)                         | Exponential backoff retry     |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Signal-based circuit breaker  |
| [mostlylucid.ephemeral.patterns.backpressure](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure)     | Signal-driven backpressure    |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                               | All packages in one DLL       |

## Target Frameworks

.NET 6.0, 7.0, 8.0, 9.0, 10.0

## License

Unlicense (public domain)
