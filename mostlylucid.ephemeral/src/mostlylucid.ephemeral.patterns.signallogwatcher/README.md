# Mostlylucid.Ephemeral.Patterns.SignalLogWatcher

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.signallogwatcher.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher)

Watches a SignalSink for matching signals and triggers callbacks. Useful for error monitoring and alerting.

```bash
dotnet add package mostlylucid.ephemeral.patterns.signallogwatcher
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalLogWatcher;

var sink = new SignalSink();

await using var watcher = new SignalLogWatcher(
    sink,
    evt => Console.WriteLine($"Error: {evt.Signal}"),
    pattern: "error.*",
    pollInterval: TimeSpan.FromMilliseconds(100));

// When any signal matching "error.*" appears, the callback fires
// Automatically deduplicates to avoid repeated callbacks for same signal
```

---

## All Options

```csharp
new SignalLogWatcher(
    // Required: signal sink to watch
    sink: signalSink,

    // Required: callback when matching signal found
    onMatch: evt => HandleSignal(evt),

    // Glob pattern to match signals
    // Default: "error.*"
    pattern: "error.*",

    // How often to poll the sink
    // Default: 200ms
    pollInterval: TimeSpan.FromMilliseconds(200)
)
```

---

## API Reference

```csharp
// Constructor starts watching immediately
var watcher = new SignalLogWatcher(sink, callback, pattern, pollInterval);

// Dispose to stop watching
await watcher.DisposeAsync();
```

---

## How It Works

```
SignalSink: [error.db] [info.request] [error.api] [error.api] [warn.memory]
                │                           │           │
                ▼                           ▼           │
            Callback                    Callback        │
                                                        ▼
                                                  (deduplicated)

Poll every 200ms:
  - Get all signals from sink
  - Match against pattern "error.*"
  - Fire callback for NEW matches only
  - Track seen signals to prevent duplicates
```

---

## Example: Error Alerting

```csharp
var sink = new SignalSink();

await using var watcher = new SignalLogWatcher(
    sink,
    async evt =>
    {
        await alertService.SendSlackAlert(
            $"Error detected: {evt.Signal}",
            $"Operation: {evt.OperationId}, Time: {evt.Timestamp}");
    },
    pattern: "error.*",
    pollInterval: TimeSpan.FromSeconds(1));

// All error signals trigger alerts
sink.Raise("error.database.connection");
sink.Raise("error.api.timeout");
```

---

## Example: Multiple Watchers

```csharp
var sink = new SignalSink();

// Watch for errors
await using var errorWatcher = new SignalLogWatcher(
    sink,
    evt => logger.LogError("Error signal: {Signal}", evt.Signal),
    pattern: "error.*");

// Watch for performance issues
await using var perfWatcher = new SignalLogWatcher(
    sink,
    evt => metrics.RecordSlowOperation(evt.Signal),
    pattern: "perf.slow.*");

// Watch for security events
await using var securityWatcher = new SignalLogWatcher(
    sink,
    evt => securityLog.Record(evt),
    pattern: "security.*",
    pollInterval: TimeSpan.FromMilliseconds(50));  // Fast polling for security
```

---

## Example: With Coordinator

```csharp
var sink = new SignalSink();

await using var coordinator = new EphemeralWorkCoordinator<Request>(
    async (req, ct) =>
    {
        try
        {
            await ProcessRequest(req, ct);
        }
        catch (Exception ex)
        {
            sink.Raise($"error.request.{ex.GetType().Name}");
            throw;
        }
    },
    new EphemeralOptions { Signals = sink });

await using var watcher = new SignalLogWatcher(
    sink,
    evt => Console.WriteLine($"Request failed: {evt.Signal}"),
    pattern: "error.request.*");

// Process requests - errors automatically logged
foreach (var req in requests)
    await coordinator.EnqueueAsync(req);
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.anomalydetector](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.anomalydetector) | Anomaly detection |
| [mostlylucid.ephemeral.patterns.telemetry](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.telemetry) | Telemetry integration |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
