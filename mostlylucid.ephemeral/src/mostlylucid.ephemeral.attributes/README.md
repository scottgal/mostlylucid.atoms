# Mostlylucid.Ephemeral.Attributes

Attribute-driven, signal-aware jobs that wire themselves into an `EphemeralWorkCoordinator`.

This package exposes:

| API                                | What it does                                                                                                                                                       |
|------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `[EphemeralJob("signal.pattern")]` | Decorates a method (returning `Task` or `ValueTask`) with the matching signal pattern trigger. Combines optional `CancellationToken` and `SignalEvent` parameters. |
| `EphemeralJobScanner`              | Reflection helper that enumerates all annotated methods on a target instance and builds descriptors.                                                               |
| `EphemeralSignalJobRunner`         | Listens on a shared `SignalSink`, matches incoming signals to job descriptors, and enqueues them on an internal `EphemeralWorkCoordinator`.                        |

### Attribute knobs at a glance

- **Ordering & lanes** – `Priority`, per-job `MaxConcurrency`, and `Lane` keep hot paths composable while slower work
  stays in separate lanes (class-level defaults via `EphemeralJobsAttribute.DefaultLane` / `.DefaultMaxConcurrency`).
- **Keying & tagging** – `OperationKey`, `KeyFromSignal`, `KeyFromPayload`, and `[KeySource]` capture meaningful keys so
  logging, telemetry, or fair scheduling stays aligned.
- **Pinning & retries** – `Pin`, `ExpireAfterMs`, `AwaitSignals`, `MaxRetries`, and `RetryDelayMs` extend visibility,
  gate execution until dependencies arrive, and heal horizontally without leaking operations.
- **Signal choreography** – `EmitOnStart`, `EmitOnComplete`, and `EmitOnFailure` automatically raise downstream signals
  for your molecules, log watchers, or other coordinators.

## Examples

### Simple stage pipeline

```csharp
var signals = new SignalSink();
using var runner = new EphemeralSignalJobRunner(signals, new[] { new PipelineStages() });

[EphemeralJob("stage.ingest")]
public Task IngestAsync(SignalEvent signal)
{
    Console.WriteLine($"ingest {signal.Key}");
    signals.Raise("stage.ingest.done", key: signal.Key);
    return Task.CompletedTask;
}

[EphemeralJob("stage.transform.*")]
public Task TransformAsync(CancellationToken ct, SignalEvent signal)
{
    Console.WriteLine($"transform {signal.Key}");
    signals.Raise("stage.transform.done", key: signal.Key);
    return Task.CompletedTask;
}

[EphemeralJob("stage.finalize")]
public ValueTask FinalizeAsync() => ValueTask.CompletedTask;

signals.Raise("stage.ingest", key: "order-42");
signals.Raise("stage.transform.input");
```

### Signal-aware coordination

Use glob patterns (`stage.transform.*`), `OperationKey`, or the `KeyFrom*` helpers to keep your pipeline keyed and
prioritized (see the attribute reference below). The runner feeds all matching jobs into a bounded coordinator, so
downstream stages automatically execute as soon as the upstream signal fires.

### Job-level concurrency and retries

Each job attribute controls **that specific job type only**, not the entire queue. This allows fine-grained control:

```csharp
[EphemeralJob(
    triggerSignal: "orders.process",
    Priority = 1,              // Lower = runs first (job-level ordering)
    MaxConcurrency = 3,        // Max 3 concurrent executions of THIS job
    MaxRetries = 5,
    RetryDelayMs = 200,
    EmitOnStart = new[] { "orders.process.started" },
    EmitOnComplete = new[] { "orders.process.completed" },
    KeyFromSignal = true,
    Lane = "io:8")]            // Run in "io" lane with max 8 concurrent jobs
public async Task ProcessOrderAsync(CancellationToken ct, SignalEvent signal, OrderPayload payload)
{
    await ordersService.ProcessAsync(payload, ct).ConfigureAwait(false);
}

[EphemeralJob("orders.process", KeyFromPayload = "Order.Id")]
public Task PostProcessAsync([KeySource(PropertyPath = "Order.Id")] OrderPayload payload) => ...;
```

This snippet shows how to:

1. Prioritize certain handlers (`Priority` - lower runs first).
2. Allow limited concurrency for **this job** (`MaxConcurrency = 3` means max 3 concurrent order processors).
3. Group jobs in lanes (`Lane = "io:8"` - up to 8 concurrent jobs in the "io" lane).
4. Emit start/complete signals for downstream stages.
5. Extract keys from signals (`KeyFromSignal`) or payloads (`KeyFromPayload` / `[KeySource]`).

### Pipeline jobs with pins and keys

```csharp
[EphemeralJobs(DefaultLane = "pipeline", DefaultMaxConcurrency = 2)]
public sealed class PipelineJobs
{
    [EphemeralJob(
        triggerSignal: "orders.process",
        Priority = 1,
        MaxConcurrency = 3,
        Lane = "hot:4",
        KeyFromSignal = true,
        Pin = true,
        EmitOnComplete = new[] { "orders.processed" })]
    public Task ProcessOrderAsync(SignalEvent signal, OrderPayload payload, CancellationToken ct)
    {
        Console.WriteLine($"Processing {payload.Order.Id} in lane {signal.Signal}");
        return Task.CompletedTask;
    }

    [EphemeralJob("orders.processed", KeyFromPayload = "Order.Id")]
    public Task NotifyCustomerAsync([KeySource(PropertyPath = "Order.Id")] OrderPayload payload)
    {
        Console.WriteLine($"Notified customer for order {payload.Order.Id}");
        return Task.CompletedTask;
    }
}

var sink = new SignalSink();
await using var runner = new EphemeralSignalJobRunner(sink, new[] { new PipelineJobs() });
sink.Raise("orders.process", key: "order-42");
```

The runner keeps the pipeline alive without additional wiring: `ProcessOrderAsync` picks up hot work in the `hot:4` lane, pins its responsibility until downstream signals (e.g., `orders.processed`) arrive, and extracts the operation key from the signal. `NotifyCustomerAsync` reads the payload via `[KeySource]` so the notifier stays keyed to the same order. Register the runner with `services.AddEphemeralSignalJobRunner<PipelineJobs>()` so DI keeps the sink, runner, and attribute descriptors aligned with your other services.

### Lanes for workload separation

Use lanes to separate different types of work (I/O-bound, CPU-bound, fast, slow):

```csharp
[EphemeralJobs(DefaultLane = "io")]
public class DataProcessor
{
    // Inherits lane="io" from class
    [EphemeralJob("file.read")]
    public Task ReadFileAsync() => ...;

    // Override to CPU-intensive lane with max 4 concurrent
    [EphemeralJob("data.compute", Lane = "cpu:4")]
    public Task ComputeAsync() => ...;

    // Fast lane for quick operations
    [EphemeralJob("cache.get", Lane = "fast")]
    public Task GetCacheAsync() => ...;
}
```

### Logging watcher pipeline

```csharp
[EphemeralJob("log.error.*")]
public Task RaiseIncidentAsync(SignalEvent signal)
{
    Console.WriteLine($"alerting on {signal.Signal} for {signal.Key}");
    signals.Raise("incident.escalate", key: signal.Key);
    return Task.CompletedTask;
}

[EphemeralJob("incident.escalate", EmitOnStart = new[] { "incident.monitor.start" })]
public Task CreateTicketAsync(SignalEvent signal, CancellationToken ct)
{
    return ticketService.CreateAsync(signal.Key!, ct);
}

signals.Raise("log.error.application", key: "orders");
```

This bootstraps a log watcher job that listens for `log.error.*` signals, raises an `incident.escalate` notification,
and lets downstream jobs (like ticket creation) fire automatically.

Pair this with `SignalLoggerProvider` so your shared `SignalSink` receives slugged `log.*` signals whenever
`Microsoft.Extensions.Logging` emits an error. The attribute runner then reacts to log-derived signals just like any
other, keeping log watching and alerting accessible from the same declarative API. Keep the `EphemeralSignalJobRunner`/
`SignalSink` wired at startup so the watcher handles log events emitted later in the app lifetime without extra wiring,
and any later service that raises `log.*` or related signals (like `incident.created`) will automatically trigger the
attributed jobs you already registered.

Now that the runner is listening, any later task can raise the watched signal directly and the same pipeline fires
without extra dependencies:

```csharp
sink.Raise("log.error.orders.dbfailure", key: "orders");
```

### Pin until queried & echoes

Attribute jobs can declare `Pin = true` so the coordinator keeps their operations alive after completion. Use
`ResponsibilitySignalManager.PinUntilQueried` (default ack pattern `responsibility.ack.*` with key=`operationId`) to tie
that pin to a downstream acknowledgement, optionally adding a `description` such as “the file is ready for pickup” and a
`maxPinDuration` so the window still self-cleans if nobody arrives.

 ```csharp
 var manager = new ResponsibilitySignalManager(coordinator, sink, maxPinDuration: TimeSpan.FromMinutes(5));
 manager.PinUntilQueried(operationId, "responsibility.ack.file", description: "awaiting file pickup");
 ```

This creates a “responsibility signal” where the job announces it has handed off state (file paths, metadata, etc.) that another reader owes it. The pin keeps the operation visible until the ack signal arrives, so the coordinator never evicts the resource while it is still needed.
 
  When the ack signal arrives the pin is released automatically, eliminating races between producers and consumers.
Combine this with `OperationEchoMaker`/`OperationEchoAtom` (see `mostlylucid.ephemeral.atoms.echo`) if you need
structured “last words”: capture the key signals or typed payloads that summarize the operation before it vanishes so
molecules or auditors can still taste the soup.

For “echo-worthy” jobs you can also create a `TypedSignalSink<EchoPayload>` (sharing the same underlying sink) and let
`mostlylucid.ephemeral.atoms.echo` build and persist `OperationEchoEntry<EchoPayload>` records as operations finalize.
Just raise `typedSink.Raise("echo.capture", payload, key: signal.Key)` when your handler reaches the critical state.

For “echo-worthy” jobs you can also create a `TypedSignalSink<EchoPayload>` (sharing the same underlying sink) and let
`mostlylucid.ephemeral.atoms.echo` build and persist `OperationEchoEntry<EchoPayload>` records as operations finalize.
Just raise `typedSink.Raise("echo.capture", payload, key: signal.Key)` when your handler reaches the critical state.

## Attribute reference

### Tune concurrency, retries, and observability

`EphemeralSignalJobRunner` accepts `EphemeralOptions` for shared `SignalSink`, batching, or max-tracking limits. The
attribute can also emit start/complete/failure signals and control retries, timeouts, and pinning without extra
plumbing:

```csharp
var runnerOptions = new EphemeralOptions
{
    MaxConcurrency = 4,
    MaxTrackedOperations = 64,
    Signals = sharedRaySink
};

using var runner = new EphemeralSignalJobRunner(sharedRaySink, handlers, runnerOptions);
```

## Attribute reference

| Property                                                            | Description                                                                                                                                                                                                                                                                                |
|---------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `TriggerSignal`                                                     | Glob pattern that raises this job (`orders.*`, `cache.flush`, etc.). `EphemeralJobsAttribute.SignalPrefix` can prepend a namespace to every method in the class.                                                                                                                           |
| `OperationKey` / `KeyFromSignal` / `KeyFromPayload` / `[KeySource]` | Control how the resulting operation is tagged. Keys help group telemetry and make custom concurrency policies easier. `KeyFromPayload` reads a property path from the typed payload (`"User.Id"`), and `KeySource` lets you annotate the parameter whose ToString() should become the key. |
| `Priority`                                                          | Lower numbers run first. Useful when multiple handlers listen to the same trigger and you want deterministic ordering. **Controls this job only, not the queue.**                                                                                                                          |
| `MaxConcurrency`                                                    | Controls how many executions of **this specific job** can run in parallel; use `EphemeralJobsAttribute.DefaultMaxConcurrency` to share defaults across the class. `-1` means unlimited. **Does not affect other jobs.**                                                                    |
| `Lane`                                                              | Processing lane for workload separation. Format: `"name"` or `"name:concurrency"` (e.g., `"io"`, `"cpu:4"`). Jobs in the same lane share concurrency control. Use `EphemeralJobsAttribute.DefaultLane` for class defaults.                                                                 |
| `EmitOnStart` / `EmitOnComplete` / `EmitOnFailure`                  | Additional signals the job raises automatically, making downstream stages composable without manual `SignalSink` calls.                                                                                                                                                                    |
| `SwallowExceptions`, `MaxRetries`, `RetryDelayMs`                   | Retry helpers that convert exceptions into signals while keeping the runner alive.                                                                                                                                                                                                         |
| `Pin` / `ExpireAfterMs`                                             | Keep jobs visible in the coordinator (pin) or allow them to expire after a custom window.                                                                                                                                                                                                  |
| `AwaitSignals` / `AwaitTimeoutMs`                                   | Delay job execution until other signals are present, useful for fan-in or dependency wiring.                                                                                                                                                                                               |

Annotate a class with
`[EphemeralJobs(DefaultPriority = 1, DefaultMaxConcurrency = 2, SignalPrefix = "orders", DefaultLane = "io")]` to apply
shared defaults.

### Core job knobs

- `Priority` keeps the same trigger deterministic when multiple handlers listen to the same signal; lower numbers run
  first.
- `MaxConcurrency` limits how many executions of the job itself can run at once, while `Lane` lets you pool multiple
  jobs under shared concurrency caps.
- `OperationKey`, `KeyFromSignal`, `KeyFromPayload`, and `[KeySource]` control how the resulting operation is tagged so
  related work shares telemetry and ordering.
- `Pin`/`ExpireAfterMs` let jobs extend their visibility window (pinning them until a downstream ack or letting them
  auto-expire), making it easy to build responsibility signals without manual bookkeeping.

## Best practices

1. **Keep signals descriptive.** Use dotted prefixes and include event semantics (`orders.receive`,
   `orders.retry.failed`) so pattern matching stays readable.
2. **Chain completion signals.** Emit `EmitOnComplete` signals so downstream jobs trigger automatically instead of
   manually wiring observers.
3. **Reuse runners.** Multiple handler instances can share a single `EphemeralSignalJobRunner`; it deduplicates
   descriptors and merges priorities for you.

## Dependency Injection

Register attribute runners with the supplied extensions so the job lifecycle is managed by DI.

```csharp
// Preferred: register your job types if they have dependencies or a non-default lifetime
services.AddScoped<ConfigJobs>(); // or AddSingleton/AddTransient as appropriate

// The runner will prefer resolving an existing registration; otherwise it will instantiate the type
services.AddEphemeralSignalJobRunner<StageJobs>();
```

Why you sometimes saw `services.AddSingleton<StageJobs>()` in examples

- Historically examples showed `AddSingleton<T>()` to ensure a single instance of job handlers lived for the app
  lifetime. That pattern forces a singleton lifetime even if the job needs scoped services.
- The extensions now prefer resolving an already-registered instance from DI. This means:
    - If you want a singleton handler, register it as `AddSingleton<T>()` explicitly.
    - If your job depends on scoped services, register it as `AddScoped<T>()` and use `AddEphemeralScopedJobRunner<T>()`
      so the runner resolves fresh scoped instances per invocation.
    - If you don't register the job type, the runner will create instances using ActivatorUtilities (constructor
      injection) and treat them as effectively singletons inside the runner.

Short ASP.NET Core "4-line" example (minimal hosting)

```csharp
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// 1) Add a coordinator for background processing
services.AddEphemeralWorkCoordinator<Order>(async (o, ct) => await orderService.ProcessAsync(o, ct));

// 2) Register attribute job types (optional if no DI deps)
services.AddScoped<OrderJobs>();
// 3) Add the attribute runner that wires up signal listeners
services.AddEphemeralSignalJobRunner<OrderJobs>();

var app = builder.Build();

// 4) Controller / Minimal endpoint interacts with coordinators / signals
app.MapPost("/orders", async (Order order, IEphemeralCoordinatorFactory<Order> factory) =>
{
    var coordinator = factory.CreateCoordinator();
    await coordinator.EnqueueAsync(order);
    return Results.Accepted();
});

app.Run();
```

Notes:

- The runner prefers resolved instances when available, so `AddSingleton<T>()` is not required unless you specifically
  want a singleton.
- Use `AddEphemeralScopedJobRunner<T>()` when jobs need scoped services per invocation (e.g., DbContext).

### Assembly-scan convenience

If you prefer a one-liner to register all attributed jobs in an assembly, use the assembly-scan overload:

```csharp
// Registers all classes in the assembly that contain [EphemeralJob] methods or [EphemeralJobs] class attribute
services.AddEphemeralSignalJobRunner(typeof(OrderJobs).Assembly);

// Or the scoped runner variant (resolves jobs inside a scope per invocation):
services.AddEphemeralScopedJobRunner(typeof(OrderJobs).Assembly);
```

## Packaging

Install via NuGet: `dotnet add package mostlylucid.ephemeral.attributes`. The package is included in
`mostlylucid.ephemeral.complete` but you can also consume it standalone when you only need declarative pipelines.
