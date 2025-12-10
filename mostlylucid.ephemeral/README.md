# Mostlylucid.Ephemeral

**The engine behind Styloflow - an ephemeral workflow solution. (And Stylobot, the learning forensic semantic firewall)**

<img src="demos/mostlylucid.ephemeral.demo/testdata/logo.png" width="120" height="120">

**Fire... and Don't *Quite* Forget.**

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨

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

## Interactive Demo

**New!** Try the interactive Spectre.Console demo to see the Ephemeral Signals pattern in action:

### Download Pre-built Executable

**No .NET required!** Download single-file executables from [Releases](https://github.com/scottgal/mostlylucid.atoms/releases):

| Platform | Download |
|----------|----------|
| 🪟 Windows (x64) | `ephemeral-demo-win-x64.exe` |
| 🪟 Windows (ARM64) | `ephemeral-demo-win-arm64.exe` |
| 🐧 Linux (x64) | `ephemeral-demo-linux-x64` |
| 🐧 Linux (ARM64) | `ephemeral-demo-linux-arm64` |
| 🍎 macOS (Intel) | `ephemeral-demo-macos-x64` |
| 🍎 macOS (Apple Silicon) | `ephemeral-demo-macos-arm64` |

See [RELEASES.md](../../RELEASES.md) for installation instructions.

### Run from Source

```bash
cd demos/mostlylucid.ephemeral.demo
dotnet run
```

### What's Included

The demo showcases 10 interactive scenarios plus benchmarks:
- **Pattern Fundamentals**: Pure Notification, Context + Hint, Command Pattern
- **Advanced Patterns**: Circuit Breaker, Backpressure, Metrics & Monitoring
- **System Demos**: Multi-step pipelines, Signal chains, Dynamic rate adjustment
- **Observability**: Live signal viewer with filtering
- **Performance**: BenchmarkDotNet integration with memory diagnostics

See [demos/mostlylucid.ephemeral.demo/README.md](demos/mostlylucid.ephemeral.demo/README.md) for details.

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

        // Keep a short-lived echo of trimmed operations so watchers can replay their last signals.
        EnableOperationEcho = true,
        OperationEchoRetention = TimeSpan.FromMinutes(1),
        OperationEchoCapacity = 256,

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

### Cross-Operation Coordination via Signals

**The core pattern**: One operation emits a signal → another operation reacts by finding that operation's state → processes it.

This is how you build reactive pipelines where operations coordinate without direct coupling:

```csharp
using Mostlylucid.Ephemeral;

// Shared signal sink coordinates all operations
var signalSink = new SignalSink();

// ══════════════════════════════════════════════════════════════════════════════
// STEP 1: File processor emits "file.saved" signals with the operation ID
// ══════════════════════════════════════════════════════════════════════════════

await using var fileProcessor = new EphemeralWorkCoordinator<FileUpload>(
    async (upload, op, ct) =>
    {
        // Save file to disk
        var filePath = await SaveFileAsync(upload, ct);

        // Store the file path in operation state (mutable Dictionary<string, object?>)
        op.State["FilePath"] = filePath;
        op.State["UploadedBy"] = upload.Username;
        op.State["FileSize"] = upload.Data.Length;

        // Signal completion - other operations can now react
        op.Signal("file.saved");
    },
    new EphemeralOptions
    {
        MaxConcurrency = 8,
        SharedSignalSink = signalSink  // Share signals globally
    });

// ══════════════════════════════════════════════════════════════════════════════
// STEP 2: Thumbnail generator reacts to "file.saved" signals
// ══════════════════════════════════════════════════════════════════════════════

await using var thumbnailGenerator = new EphemeralWorkCoordinator<long>(
    async (sourceOpId, op, ct) =>
    {
        // Find the file operation by ID using the signal event
        var fileOp = fileProcessor.TryGetOperation(sourceOpId);

        if (fileOp == null)
        {
            op.Signal("error.source_not_found");
            return;
        }

        // Access the source operation's state
        var filePath = fileOp.State.GetValueOrDefault("FilePath") as string;
        var uploadedBy = fileOp.State.GetValueOrDefault("UploadedBy") as string;

        if (filePath == null)
        {
            op.Signal("error.no_file_path");
            return;
        }

        op.Signal("thumbnail.generating");

        // Generate thumbnail from the saved file
        var thumbPath = await CreateThumbnailAsync(filePath, ct);

        op.State["ThumbnailPath"] = thumbPath;
        op.State["SourceFile"] = filePath;
        op.State["ProcessedFor"] = uploadedBy;

        op.Signal("thumbnail.complete");
    },
    new EphemeralOptions
    {
        MaxConcurrency = 4,
        SharedSignalSink = signalSink
    });

// ══════════════════════════════════════════════════════════════════════════════
// STEP 3: Wire up the reactive flow - subscribe to signals
// ══════════════════════════════════════════════════════════════════════════════

signalSink.Subscribe(signal =>
{
    // When a file is saved, trigger thumbnail generation
    if (signal.Is("file.saved"))
    {
        // Enqueue the source operation ID so thumbnail generator can find it
        _ = thumbnailGenerator.EnqueueAsync(signal.OperationId);
    }
});

// ══════════════════════════════════════════════════════════════════════════════
// USAGE: Start the pipeline
// ══════════════════════════════════════════════════════════════════════════════

await fileProcessor.EnqueueAsync(new FileUpload
{
    Filename = "photo.jpg",
    Data = imageBytes,
    Username = "alice"
});

// The flow:
// 1. fileProcessor saves file → stores path in op.State → signals "file.saved"
// 2. Signal subscription triggers → thumbnailGenerator.EnqueueAsync(opId)
// 3. thumbnailGenerator finds the file operation → reads State["FilePath"] → generates thumbnail
```

**Why this pattern matters:**

- ✅ **Decoupled**: Operations don't reference each other directly
- ✅ **Observable**: Every step emits signals for monitoring/debugging
- ✅ **Stateful**: Operation state persists in the window for downstream access
- ✅ **Reactive**: Signal subscriptions create automatic coordination
- ✅ **Scalable**: Each coordinator has independent concurrency controls

**Alternative: Using pattern matching for complex pipelines**

```csharp
signalSink.Subscribe(signal =>
{
    // Match multiple signal patterns
    if (signal.Signal.StartsWith("file."))
    {
        if (signal.Is("file.saved"))
            _ = thumbnailGenerator.EnqueueAsync(signal.OperationId);
        else if (signal.Is("file.deleted"))
            _ = cleanupCoordinator.EnqueueAsync(signal.OperationId);
        else if (signal.Signal.StartsWith("file.error."))
            _ = errorHandler.EnqueueAsync(signal.OperationId);
    }
});
```

See [examples/](Examples/) for complete multi-stage pipeline demonstrations.

### Cross-Operation Coordination with Attribute Jobs

The same reactive pattern works beautifully with attribute-driven jobs - even cleaner syntax:

```csharp
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Attributes;

public class FileProcessingPipeline
{
    private readonly SignalSink _signalSink;
    private readonly EphemeralWorkCoordinator<FileUpload> _fileProcessor;

    public FileProcessingPipeline(SignalSink signalSink)
    {
        _signalSink = signalSink;

        // File processor stores state and emits signals
        _fileProcessor = new EphemeralWorkCoordinator<FileUpload>(
            async (upload, op, ct) =>
            {
                var filePath = await SaveFileAsync(upload, ct);

                // Store state for downstream jobs
                op.State["FilePath"] = filePath;
                op.State["UploadedBy"] = upload.Username;
                op.State["MimeType"] = upload.ContentType;

                op.Signal("file.saved");  // Triggers attribute jobs
            },
            new EphemeralOptions
            {
                MaxConcurrency = 8,
                SharedSignalSink = signalSink
            });
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Attribute jobs automatically react to signals and access source operation state
    // ═══════════════════════════════════════════════════════════════════════════════

    [EphemeralJob(
        "file.saved",                    // React to this signal
        MaxConcurrency = 4,              // Parallel thumbnail generation
        EmitOnStart = "thumbnail.start",
        EmitOnComplete = "thumbnail.complete")]
    public async Task GenerateThumbnail(SignalEvent signal, EphemeralOperation op, CancellationToken ct)
    {
        // Find the source file operation using the signal's OperationId
        var fileOp = _fileProcessor.TryGetOperation(signal.OperationId);

        if (fileOp == null)
        {
            op.Signal("error.source_not_found");
            return;
        }

        // Access the file operation's state
        var filePath = fileOp.State.GetValueOrDefault("FilePath") as string;
        var mimeType = fileOp.State.GetValueOrDefault("MimeType") as string;

        if (filePath == null || !mimeType?.StartsWith("image/") == true)
        {
            op.Signal("skipped.not_image");
            return;
        }

        // Generate thumbnail
        var thumbPath = await CreateThumbnailAsync(filePath, ct);

        // Store result in this operation's state
        op.State["ThumbnailPath"] = thumbPath;
        op.State["SourceFile"] = filePath;
    }

    [EphemeralJob(
        "file.saved",
        MaxConcurrency = 2,
        EmitOnComplete = "virus_scan.complete")]
    public async Task ScanForVirus(SignalEvent signal, EphemeralOperation op, CancellationToken ct)
    {
        var fileOp = _fileProcessor.TryGetOperation(signal.OperationId);
        if (fileOp == null) return;

        var filePath = fileOp.State.GetValueOrDefault("FilePath") as string;
        if (filePath == null) return;

        var isClean = await VirusScanAsync(filePath, ct);

        if (!isClean)
        {
            op.Signal("virus.detected");
            await DeleteFileAsync(filePath, ct);
        }
        else
        {
            op.Signal("virus.clean");
        }

        op.State["VirusScanResult"] = isClean ? "clean" : "infected";
    }

    [EphemeralJob(
        "file.saved",
        MaxConcurrency = 1,
        Lane = "database",  // Serialize database writes
        EmitOnComplete = "metadata.saved")]
    public async Task SaveMetadata(SignalEvent signal, EphemeralOperation op, CancellationToken ct)
    {
        var fileOp = _fileProcessor.TryGetOperation(signal.OperationId);
        if (fileOp == null) return;

        await _database.SaveFileMetadataAsync(new FileMetadata
        {
            FilePath = fileOp.State.GetValueOrDefault("FilePath") as string,
            UploadedBy = fileOp.State.GetValueOrDefault("UploadedBy") as string,
            MimeType = fileOp.State.GetValueOrDefault("MimeType") as string,
            UploadedAt = DateTimeOffset.UtcNow
        }, ct);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Bootstrap the pipeline
// ═══════════════════════════════════════════════════════════════════════════════

var signalSink = new SignalSink();
var pipeline = new FileProcessingPipeline(signalSink);

// Register attribute jobs
await using var runner = new EphemeralSignalJobRunner(signalSink, new[] { pipeline });

// Upload a file - triggers the entire pipeline automatically
await pipeline._fileProcessor.EnqueueAsync(new FileUpload
{
    Filename = "photo.jpg",
    Data = imageBytes,
    Username = "alice",
    ContentType = "image/jpeg"
});

// All three jobs (thumbnail, virus scan, metadata) run in parallel automatically!
```

**Benefits of attribute jobs for cross-operation patterns:**

- ✅ **Declarative**: Signal patterns, concurrency, and emissions defined in attributes
- ✅ **Automatic wiring**: No manual `Subscribe()` calls - jobs auto-register
- ✅ **Same state access**: Use `TryGetOperation(signal.OperationId)` just like coordinators
- ✅ **Lanes**: Serialize specific jobs (like database writes) while others run in parallel
- ✅ **Self-documenting**: Attributes show the reactive flow at a glance

See [mostlylucid.ephemeral.attributes](src/mostlylucid.ephemeral.attributes/README.md) for complete attribute documentation.

## Registering in dependency injection

The familiar `AddX` helpers make it easy to drop coordinators and attribute runners into ASP.NET Core without reshaping your hosting code:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

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

`services.AddCoordinator<T>(...)` and its scoped/keyed variants simply wrap the `AddEphemeral…` helpers with a familiar surface, keeping registration consistent with other ASP.NET Core services.

## Attribute-driven jobs

`mostlylucid.ephemeral.attributes` ships with the core packages and lets you decorate methods with `[EphemeralJob]` or `[EphemeralJobs]` to react to `SignalSink` events declaratively. Treat this attribute-driven runner as part of the same canonical surface: handlers join the signal window, signal cache, logging adapters, and responsibility/pinning story you build elsewhere. Each attribute can declare `Priority`, job-level `MaxConcurrency`, `Lane`, key extraction, signal emissions, pinning, retries, and sliding expiry so your pipeline self-composes without extra plumbing.

```csharp
var sink = new SignalSink();
await using var runner = new EphemeralSignalJobRunner(sink, new[] { new LogWatcherJobs(sink) });

// Bootstrapped at startup – the runner now listens for matching signals as the app runs.
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddProvider(new SignalLoggerProvider(new TypedSignalSink<SignalLogPayload>(sink)));
});

var logger = loggerFactory.CreateLogger("orders");
logger.LogError(new EventId(1001, "DbFailure"), "Order store failed");

// Any subsequent task that raises `log.error.*` or wired signals automatically enqueues the attributed jobs.
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

```csharp
[EphemeralJobs(SignalPrefix = "pipeline", DefaultLane = "io", DefaultMaxConcurrency = 2)]
public sealed class PipelineJobs
{
    [EphemeralJob("orders.process", Priority = 1, MaxConcurrency = 3, Lane = "hot:4", KeyFromSignal = true, Pin = true, EmitOnComplete = new[] { "orders.processed" })]
    public Task ProcessOrderAsync(SignalEvent signal, OrderPayload payload, CancellationToken ct)
    {
        Console.WriteLine($"Processing {signal.Key}");
        return Task.CompletedTask;
    }

    [EphemeralJob("orders.processed", KeyFromPayload = "Order.Id")]
    public Task NotifyCustomerAsync([KeySource(PropertyPath = "Order.Id")] OrderPayload payload)
    {
        Console.WriteLine($"Notified order {payload.Order.Id}");
        return Task.CompletedTask;
    }
}

var pipelineSink = new SignalSink();
await using var pipelineRunner = new EphemeralSignalJobRunner(pipelineSink, new[] { new PipelineJobs() });
pipelineSink.Raise("orders.process", key: "order-42");
```

This sample shows how attribute jobs can prioritize hot handlers, pin their responsibility until downstream acks, and extract meaningful keys from signals or payloads via `KeyFromSignal`, `KeyFromPayload`, and `[KeySource]`.

This bootstraps a watcher at application start and lets later tasks (or logger events) trigger the pipeline without wiring. Attribute jobs can also declare keys via `OperationKey`, `KeyFromSignal`, `KeyFromPayload`, or `[KeySource]`, emit lifecycle signals, pin work until acked, and slot into named lanes.

Key knobs include:

- **Ordering & lanes**: `Priority`, `MaxConcurrency`, and `Lane` let you keep hot paths constrained while other jobs run in parallel. `EphemeralJobsAttribute.DefaultLane` / `DefaultMaxConcurrency` let you share defaults across a handler class.
- **Tagging & routing**: `OperationKey`, `KeyFromSignal`, `KeyFromPayload`, and `[KeySource]` capture meaningful keys so operations stay grouped, aiding logging, reporting, and fair scheduling.
- **Responsibility & retries**: `Pin`, `ExpireAfterMs`, `AwaitSignals`, and `AwaitTimeoutMs` extend visibility or gate execution until dependencies arrive. `MaxRetries`, `RetryDelayMs`, and `SwallowExceptions` keep the runner resilient while emitting signals you can trace.
- **Signal choreography**: `EmitOnStart`, `EmitOnComplete`, and `EmitOnFailure` publish lifecycle signals instantly so downstream jobs, molecules, or log watchers can follow the breadcrumbs.

Use `services.AddEphemeralSignalJobRunner<T>()` (or the scoped variant) so DI owns the sink and runner; `services.AddCoordinator<T>(…)` + `services.AddScopedCoordinator<T>(…)` helpers mirror the classic `AddX` surface for coordinators.

When a job sets `Pin = true`, the operation stays visible until a downstream signal releases it. Combine this with `ResponsibilitySignalManager.PinUntilQueried` (default ack `responsibility.ack.*` keyed by the operation ID) so you can declare “I saved this file, hold it until somebody acknowledges the pointer.” Append `ExpireAfterMs` to bound the pin, and let `OperationEchoMaker` capture the final signal payloads (“last words”) so scouts or auditors can still read the state once the job is trimmed.

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

Attribute jobs emit completion/failure signals, support job-level priority/concurrency/lanes, and plug into the same caches, log adapters, and workflows you already build with signals.

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

### Operation-Scoped Signal Emission

When using `EphemeralForEachAsync` with the operation-exposing overload, you can emit signals that are automatically scoped to the current operation ID. This ensures all signals from a single operation share the same ID for proper correlation:

```csharp
var sink = new SignalSink();

// Process jobs with operation-scoped signals
await jobs.EphemeralForEachAsync(async (job, op) =>
{
    // All signals from this operation will share the same operation ID
    op.Signal($"resize.{job.SizeName}.started");

    // ... do work ...

    op.Signal($"resize.{job.SizeName}.complete");
    op.Signal($"file.saved:{outputPath}");
},
new EphemeralOptions { MaxConcurrency = Environment.ProcessorCount },
sink);

// Query signals by operation ID (shortcut method)
var signalsForOp = sink.GetOpSignals(targetOpId);

// Or filter by pattern too
var resizeSignals = sink.GetOpSignals(targetOpId, "resize.*");
```

**Key Benefits:**
- **Automatic correlation**: All signals from one operation share the same operation ID
- **Nested coordinators**: Sub-operations get unique IDs while maintaining parent relationship
- **Signal-based stats**: Derive metrics directly from signal history instead of separate counters

See [PARALLEL_RESIZE_DEMO.md](demos/mostlylucid.ephemeral.demo/PARALLEL_RESIZE_DEMO.md) for a complete example of nested coordinators with operation-scoped signals.

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

Modern DI roots may prefer the shorter helpers such as `services.AddCoordinator<T>(...)`, `services.AddScopedCoordinator<T>(...)`, or `services.AddKeyedCoordinator<T, TKey>(...)` since they read like regular `AddX` calls while delegating to the same Ephemeral-specific registrations under the hood.

See also: `docs/Services.md` for a DI-focused guide with examples and best practices.

## Lane + Key Configuration (Simple config, hidden power)

Priority-aware coordinators let you treat the `AddCoordinator` helpers as the chef, the lane name as the bowl (hot/normal/cold), and the key selector as the per-entity handle. You keep the familiar `EphemeralOptions` surface but gain signal-aware gates, named lanes, and hidden concurrency limits without rewriting your body logic.

```csharp
var sink = new SignalSink();
var lanes = new[]
{
    new PriorityLane("hot:4", CancelOnSignals: new HashSet<string> { "maintenance" }),
    new PriorityLane("normal"),
    new PriorityLane("slow:2")
};

await using var coordinator = new PriorityWorkCoordinator<WorkItem>(
    new PriorityWorkCoordinatorOptions<WorkItem>(
        async (item, ct) => await processor.ProcessAsync(item, ct),
        lanes,
        new EphemeralOptions { Signals = sink }));

await coordinator.EnqueueAsync(new WorkItem("order-42"), laneName: "hot");   // hot path
await coordinator.EnqueueAsync(new WorkItem("order-43"), laneName: "slow");  // cold lane
```

If you need per-key ordering, drop in `PriorityKeyedWorkCoordinator`. Reuse the same `lanes` array, plug in a `keySelector`, and the coordinator still respects lanes while keeping every key sequential.

```csharp
var keyed = new PriorityKeyedWorkCoordinator<WorkItem, string>(
    new PriorityKeyedWorkCoordinatorOptions<WorkItem, string>(
        order => order.CustomerId,
        async (order, ct) => await processor.ProcessPerCustomerAsync(order, ct),
        lanes,
        new EphemeralOptions { Signals = sink }));

await keyed.EnqueueAsync(new WorkItem("order-42") { CustomerId = "A" }, laneName: "hot");
await keyed.EnqueueAsync(new WorkItem("order-44") { CustomerId = "B" }, laneName: "normal");
```

Those lane names (and optional `MaxDepth`, `CancelOnSignals`, `DeferOnSignals`) become part of your observability surface—traffic counts, logs, and signals can all report “hot” vs. “slow” without any ceremony. Combine with the keyed helpers so the hidden power of lanes is still available even when the coordinator is grouped by customer, tenant, or any other key.

## Logging & Signals

`mostlylucid.ephemeral.logging` stitches `Microsoft.Extensions.Logging` and the signal world together.

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

// The log watcher job (see below) now sees `log.error.orders.dbfailure`
// and raises downstream signals for escalation, while SignalToLoggerAdapter
// can mirror those signals back into the ILogger pipeline if desired.
```

```csharp
public sealed class LogWatcherJobs
{
    private readonly SignalSink _sink;

    public LogWatcherJobs(SignalSink sink) => _sink = sink;

    [EphemeralJob("log.error.*")]
    public Task EscalateAsync(SignalEvent signal)
    {
        Console.WriteLine($"Escalating {signal.Signal} for {signal.Key}");
        _sink.Raise("incident.created", key: signal.Key);
        return Task.CompletedTask;
    }

    [EphemeralJob("incident.created")]
    public Task NotifyAsync(SignalEvent signal)
    {
        Console.WriteLine($"Notified incident for {signal.Key}");
        return Task.CompletedTask;
    }
}
```

Use `SignalToLoggerAdapter` when you want signals (including the ones emitted by the jobs above) to appear in normal logs with inferred severity and operation metadata.

## Examples

### Circuit Breaker Pattern
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
| `mostlylucid.ephemeral.atoms.volatile` | Instantly evicts operations that emit a kill signal so high-throughput work stays untracked |
| `mostlylucid.ephemeral.atoms.windowsize` | Signal-based window/retention adjustments for shared sink windows |
| `mostlylucid.ephemeral.atoms.windowsize` | Signals can grow/shrink the signal window/retention (window size atom) |
| `mostlylucid.ephemeral.atoms.molecules` | Molecules/atom-trigger helpers for signal-driven workflows |
| `mostlylucid.ephemeral.atoms.scheduledtasks` | Cron/JSON-driven durable tasks using `DurableTaskAtom` + `ScheduledTasksAtom` |
| `mostlylucid.ephemeral.atoms.echo` | Capture typed "last words" payloads via `OperationEchoMaker` and persist them with `OperationEchoAtom` |
| `mostlylucid.ephemeral.atoms.ratelimit` | Token bucket and GCRA rate limiting with signal integration - [GCRA docs](src/mostlylucid.ephemeral.atoms.ratelimit/GCRA.md) |

### Scheduled tasks

The `mostlylucid.ephemeral.atoms.scheduledtasks` package turns cron definitions (or JSON files of schedules) into durable, pinned work. `DurableTaskAtom` tracks each job inside its own coordinator, and `ScheduledTasksAtom` uses `ScheduledTaskDefinition.LoadFromJsonFile` (cron, signal, key, payload, timezone, `runOnStartup`, etc.) to raise a `DurableTask` that in turn emits the configured signal. Keep the runner alive at startup so downstream coordinators, log watchers, or molecules can respond to the emitted `signal.*` events without extra glue.

Each `DurableTask` is annotated with the schedule `Name`, emitted `Signal`, optional `Key`, `Payload`, and human-readable `Description`. Downstream listeners treat that signal as the job handle—no extra wiring is needed to pass along filenames, URLs, or metadata. When you just need to wait for every scheduled task to finish firing (for example, in a test), call `DurableTaskAtom.WaitForIdleAsync()` so you can observe `PendingCount`/`ActiveCount` without stopping the atom.

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

Configuring `MemoryCacheEntryOptions.SlidingExpiration` lets you approximate a sliding window (each hit refreshes the TTL), but the built-in cache never signals why operations were evicted nor extends TTLs for hot keys. `EphemeralLruCache` (the default in this repo) adds hot-key amplification, signal telemetry, and background eviction so you get both the sliding window feel and the self-optimizing behavior you need for observability.

### VolatileOperationAtom

> **Package:** [mostlylucid.ephemeral.atoms.volatile](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.volatile)

Immediately drops operations the moment they raise a kill signal so high-throughput tasks never bloat the tracked window. The atom watches the shared `SignalSink`, calls `IOperationEvictor.TryKill` on the operation ID carried by the matching signal, and still lets the coordinator emit the final echoes before the entry disappears.

```csharp
var sink = new SignalSink();
await using var coordinator = new EphemeralWorkCoordinator<JobItem>(
    async (job, ct) => await QuickProcessAsync(job, ct),
    new EphemeralOptions
    {
        Signals = sink,
        EnableOperationEcho = true,
        OperationEchoRetention = TimeSpan.FromSeconds(30)
    });

using var volatileAtom = new VolatileOperationAtom(sink, coordinator);

// inside the job: emitter.Emit("kill.job");
```

Combine it with `OperationEchoMaker`/`OperationEchoAtom` and typed `*.echo.*` signals so only the tiny echo you care about survives after the kill drops the rest.

### Window Size Atom

> **Package:** [mostlylucid.ephemeral.atoms.windowsize](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.windowsize)

Raise `window.size.*` or `window.time.*` commands on a shared `SignalSink` to expand/shrink the tracked capacity and retention age with the same semantics as you use on rate limit signals.

```csharp
var sink = new SignalSink(maxCapacity: 200, maxAge: TimeSpan.FromMinutes(1));
await using var atom = new WindowSizeAtom(sink);

sink.Raise("window.size.increase:100");
sink.Raise("window.time.set:00:05:00");
```

The atom clamps every change through `SignalSink.UpdateWindowSize`, so you can safely wire it to backpressure/event signals without duplicating parsing logic.

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

> Tip: `MemoryCache` can also be configured with sliding expiration via `MemoryCacheEntryOptions`, but it never tracks hot keys or emits the cache signals that `EphemeralLruCache` does. When you want the cache to self-focus and surface hot/cold telemetry, the LRU cache is the default for `SqliteSingleWriter` and the general recommendation.

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
|  - MoleculeFactory & blueprints                              |
+-------------------------------------------------------------+
```

## Molecules: reusable workflows

See `mostlylucid.ephemeral.atoms.molecules` for the blueprint/runner pattern plus the `AtomTrigger` helper that lets atoms start other atoms whenever matching signals arrive. `MoleculeBlueprintBuilder` lets you describe the steps (the ingredients) and wire signals between them, while `MoleculeRunner` listens for your trigger signal and executes each atom in order inside the shared coordinator window and DI scope. `AtomTrigger` can then watch for signals to drop new atoms or even spin up another molecule when a step emits a follow-up signal.

Molecule steps can also emit the typed echoes (`mostlylucid.ephemeral.atoms.echo`) when they finish. Think of that echo as the molecule shouting to the parent chef/coordinator (“taste the soup”). If the chef likes it, add more ingredients or signal “served”; if it fails, the chef can retrigger another molecule or raise alerts. This keeps the whole hierarchy cooking together—from chef to soup to molecules to atoms.
 
## How the stack cooks

Think of the runtime like a kitchen: the coordinator is the chef that stands over the working window (the soup) capturing the recent operations and signals. Molecules are the ready-to-make assemblies of atoms (pre-assembled kits the chef can drop in when a trigger signal arrives), and individual atoms are the discrete work pieces that each molecule (or the chef directly) orchestrates to finish a dish. This hierarchy keeps things composable: coordinators/chefs own the window, molecules orchestrate multiple atoms, and the atoms themselves raise signals or trigger further molecules through `AtomTrigger`.

## Memory Model

- Operations stored in `ConcurrentQueue<EphemeralOperation>` with bounded window
- `MaxTrackedOperations` limits window size; old operations evict automatically (LRU)
- `MaxOperationLifetime` controls how long completed operations stay visible
- Pinning (`Pin(id)`) prevents eviction for important operations
- `GetEchoes()` returns the short-lived “echo” copy of the final signal wave when `EnableOperationEcho` is true so observers can replay the last messages just after the operation dies.

```csharp
// Replay the last traces of trimmed ops (enabled by EnableOperationEcho)
var echoSignals = coordinator.GetEchoes(pattern: "error.*")
    .Where(e => e.Timestamp > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))
    .ToList();

if (echoSignals.Count > 0)
{
    logger.LogWarning("Recent trimmed errors: {Count}", echoSignals.Count);
}
```

`EnableOperationEcho`, `OperationEchoRetention`, and `OperationEchoCapacity` control how long echoes live and how many the coordinator keeps, so you can balance replay observability with a small memory footprint.

- When operations are trimmed (size or age), the coordinator raises `OperationFinalized`. Think of it as the operation's *last words*—subscribe to the event to log diagnostics, emit a final signal, or clean up external resources before the entry disappears.

```csharp
coordinator.OperationFinalized += snapshot =>
{
    if (snapshot.IsPinned)
    {
        sink.Raise("responsibility.finalized", key: snapshot.Key);
    }
    logger.LogInformation("Finalizing {Signal} (op:{Id}) with {Count} signal(s)", snapshot.Key ?? "none", snapshot.OperationId, snapshot.Signals?.Count ?? 0);
};
```

If you need to persist a tiny “last words” record before the operation departs, spin up a dedicated note atom:

```csharp
using Mostlylucid.Ephemeral.Patterns;

var notes = new LastWordsNoteAtom(async note => await storage.AppendAsync(note));

coordinator.OperationFinalized += snapshot =>
{
    var note = new LastWordsNote(
        OperationId: snapshot.OperationId,
        Key: snapshot.Key,
        Signal: snapshot.Signals?.FirstOrDefault(),
        Timestamp: DateTimeOffset.UtcNow,
        Metadata: new Dictionary<string, string?> { ["Reason"] = snapshot.IsPinned ? "Responsibility" : "Evicted" });

    _ = notes.EnqueueAsync(note);
};
```

`LastWordsNote` stays small (just the op id, key, timestamp, and optional metadata), and the note atom serializes acceptance so you can persist fatal-state externally while the coordinator trims the window.
```

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

Use `ResponsibilitySignalManager` with your sink/coordinator to `PinUntilQueried` (default ack signal: `responsibility.ack.*` with key=`operationId`). You can pin by operation ID and ack pattern, add an optional `description` (like “I saved this file, I’m waiting for someone to see where it landed”), and bound the pin with `maxPinDuration` (e.g., `TimeSpan.FromMinutes(5)`) so the window stays self-cleaning even when readers are slow.

```csharp
var manager = new ResponsibilitySignalManager(coordinator, sink, maxPinDuration: TimeSpan.FromMinutes(5));
if (manager.PinUntilQueried(operationId, "file.ready", ackKey: fileId, description: $"Awaiting read of {fileId}"))
{
    sink.Raise("file.ready", key: fileId);
}

// Downstream consumer acknowledges the work
sink.Raise("file.ready.ack", key: fileId);
```

Call `CompleteResponsibility(operationId)` when you want to release the pin manually (for retries or downstream failures). The manager unpins automatically when the ack signal arrives, and the coordinator still raises `OperationFinalized` when the entry finally leaves the window so you can capture its “last words” (logs, signals, etc.) before cleanup.

## Echo Maker: capture the “last words”

When you want to archive the most critical state that an operation emits before it vanishes, use `mostlylucid.ephemeral.atoms.echo`. It hooks `TypedSignalSink<TPayload>` into `OperationFinalized`, tracks the typed payloads captured for your chosen signals, and hands you `OperationEchoEntry<TPayload>` records you can persist or alert on.

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

[EphemeralJob("job.done")]
public Task OnJobDone(SignalEvent signal)
{
    typedSink.Raise("echo.capture", new EchoPayload(signal.Key, "archivable"), key: signal.Key);
    return Task.CompletedTask;
}
```

The activation signal (e.g., `echo.capture`) marks the point at which the maker begins tracking the operation, and `CaptureSignalPattern` / `CapturePredicate` let you pare down the payload stream. Attribute handlers simply raise the typed signal with whatever “most critical state” they want echoed, and the atom serializes a bounded, durable note before the coordinator trims the entry.

`EchoPayload` is your own compact record (order id, status, minimal metadata, etc.).

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

## Documentation

### Best Practices Guide

**NEW**: See [BEST_PRACTICES.md](BEST_PRACTICES.md) for comprehensive guidance on building signal-based systems:

- **Signal Architecture**: Three-level hierarchical scoping (Sink → Coordinator → Atom)
- **Coordinator Patterns**: Avoiding common anti-patterns (shared sinks, name collisions)
- **Atom Development**: Lifecycle management, composition strategies
- **Performance Optimization**: Hot path considerations, memory management, span-based parsing
- **Testing**: Unit and integration test patterns
- **Common Pitfalls**: Operation ID filtering, subscription disposal, blocking handlers

### Additional Resources

- [SIGNALS_PATTERN.md](SIGNALS_PATTERN.md) - Deep dive into the three signal models (Pure Notification, Context+Hint, Command)
- [CLAUDE.md](CLAUDE.md) - Project architecture and build instructions
- [demos/mostlylucid.ephemeral.demo/README.md](demos/mostlylucid.ephemeral.demo/README.md) - Interactive demo documentation
- [REVIEW_FINDINGS.md](REVIEW_FINDINGS.md) - Recent code review results and recommendations

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
