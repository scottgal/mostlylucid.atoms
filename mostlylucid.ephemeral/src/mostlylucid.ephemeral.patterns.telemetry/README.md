# Mostlylucid.Ephemeral.Patterns.Telemetry

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.telemetry.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.telemetry)

Async signal handler for telemetry integration with non-blocking I/O.

```bash
dotnet add package mostlylucid.ephemeral.patterns.telemetry
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.Telemetry;

var telemetryClient = new MyTelemetryClient();
await using var handler = new TelemetrySignalHandler(telemetryClient);

// Wire up to coordinator signals
var options = new EphemeralOptions
{
    OnSignal = evt => handler.OnSignal(evt)
};

// Signals are processed in background, never blocking the main work
```

---

## All Options

```csharp
new TelemetrySignalHandler(
    // Required: telemetry client implementation
    telemetry: telemetryClient
)

// Internal configuration:
// - maxConcurrency: 8 (parallel telemetry calls)
// - maxQueueSize: 5000 (bounded queue)
```

---

## API Reference

```csharp
// Handle a signal (non-blocking, returns immediately)
bool accepted = handler.OnSignal(signalEvent);

// Check queue status
int queued = handler.QueuedCount;
long processed = handler.ProcessedCount;
long dropped = handler.DroppedCount;

// Dispose (flushes remaining signals)
await handler.DisposeAsync();
```

---

## ITelemetryClient Interface

```csharp
public interface ITelemetryClient
{
    Task TrackEventAsync(string eventName, Dictionary<string, string> properties, CancellationToken ct);
    Task TrackExceptionAsync(string exceptionType, Dictionary<string, string> properties, CancellationToken ct);
    Task TrackMetricAsync(string metricName, double value, CancellationToken ct);
}
```

---

## How It Works

```
Signal arrives ─> OnSignal() ─> [Queue] ─> AsyncProcessor ─> ITelemetryClient
                      │                         │
                      ▼                         ▼
                 Returns immediately    8 concurrent workers
                 (non-blocking)         processing telemetry
```

Signal routing:

- `error.*` signals → `TrackExceptionAsync`
- `perf.*` signals → `TrackMetricAsync`
- All other signals → `TrackEventAsync`

---

## Example: Application Insights Integration

```csharp
public class AppInsightsTelemetryClient : ITelemetryClient
{
    private readonly TelemetryClient _client;

    public AppInsightsTelemetryClient(TelemetryClient client) => _client = client;

    public Task TrackEventAsync(string eventName, Dictionary<string, string> properties, CancellationToken ct)
    {
        _client.TrackEvent(eventName, properties);
        return Task.CompletedTask;
    }

    public Task TrackExceptionAsync(string exceptionType, Dictionary<string, string> properties, CancellationToken ct)
    {
        _client.TrackException(new Exception(exceptionType), properties);
        return Task.CompletedTask;
    }

    public Task TrackMetricAsync(string metricName, double value, CancellationToken ct)
    {
        _client.TrackMetric(metricName, value);
        return Task.CompletedTask;
    }
}

// Usage
var telemetry = new AppInsightsTelemetryClient(telemetryClient);
await using var handler = new TelemetrySignalHandler(telemetry);

await using var coordinator = new EphemeralWorkCoordinator<Request>(
    ProcessRequestAsync,
    new EphemeralOptions { OnSignal = handler.OnSignal });
```

---

## Example: InMemory Testing

```csharp
// Built-in in-memory client for testing
var telemetry = new InMemoryTelemetryClient();
await using var handler = new TelemetrySignalHandler(telemetry);

// Process some work that emits signals
await using var coordinator = new EphemeralWorkCoordinator<int>(
    async (n, ct) =>
    {
        coordinator.Signal($"perf.processed:{n}");
        if (n % 10 == 0) coordinator.Signal("error.sample");
    },
    new EphemeralOptions { OnSignal = handler.OnSignal });

for (int i = 0; i < 100; i++)
    await coordinator.EnqueueAsync(i);

coordinator.Complete();
await coordinator.DrainAsync();
await handler.DisposeAsync();

// Verify telemetry
var events = telemetry.GetEvents();
var errors = events.Where(e => e.Type == TelemetryEventType.Exception);
var metrics = events.Where(e => e.Type == TelemetryEventType.Metric);
```

---

## Example: Monitoring Handler Health

```csharp
await using var handler = new TelemetrySignalHandler(telemetryClient);

// Monitor in background
Task.Run(async () =>
{
    while (true)
    {
        Console.WriteLine($"Queued: {handler.QueuedCount}");
        Console.WriteLine($"Processed: {handler.ProcessedCount}");
        Console.WriteLine($"Dropped: {handler.DroppedCount}");

        if (handler.DroppedCount > 0)
            logger.LogWarning("Telemetry signals being dropped!");

        await Task.Delay(5000);
    }
});
```

---

## Related Packages

| Package                                                                                                                           | Description        |
|-----------------------------------------------------------------------------------------------------------------------------------|--------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                     | Core library       |
| [mostlylucid.ephemeral.patterns.signallogwatcher](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher) | Signal log watcher |
| [mostlylucid.ephemeral.patterns.signalinghttp](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalinghttp)       | HTTP with signals  |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                                   | All in one DLL     |

## License

Unlicense (public domain)
