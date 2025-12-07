# Mostlylucid.Ephemeral.Patterns.AnomalyDetector

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.anomalydetector.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.anomalydetector)

Moving-window anomaly detection based on signal pattern thresholds.

```bash
dotnet add package mostlylucid.ephemeral.patterns.anomalydetector
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.AnomalyDetector;

var sink = new SignalSink();

var detector = new SignalAnomalyDetector(
    sink,
    pattern: "error.*",
    threshold: 10,
    window: TimeSpan.FromSeconds(30));

if (detector.IsAnomalous())
{
    Console.WriteLine("Too many errors in the last 30 seconds!");
}
```

---

## All Options

```csharp
new SignalAnomalyDetector(
    // Required: signal sink to monitor
    sink: signalSink,

    // Glob pattern to match signals
    // Default: "error.*"
    pattern: "error.*",

    // Number of matches to trigger anomaly
    // Default: 5
    threshold: 5,

    // Time window for counting matches
    // Default: 10 seconds
    window: TimeSpan.FromSeconds(10)
)
```

---

## API Reference

```csharp
// Check if anomaly threshold is exceeded
bool isAnomalous = detector.IsAnomalous();

// Get current count of matching signals in window
int matchCount = detector.GetMatchCount();
```

---

## How It Works

Scans a SignalSink with a moving time window and flags anomalies when the pattern match count exceeds the threshold.

```
Window: [now - 30s] ────────────────────> [now]
        error.db  error.api  error.api  error.timeout  error.api
        ──────────────────────────────────────────────────────────
        Count: 5 signals matching "error.*"
        Threshold: 10
        Result: NOT anomalous (5 < 10)
```

---

## Example: Error Monitoring

```csharp
var sink = new SignalSink();

var detector = new SignalAnomalyDetector(
    sink,
    pattern: "error.*",
    threshold: 5,
    window: TimeSpan.FromMinutes(1));

// Monitor loop
while (true)
{
    if (detector.IsAnomalous())
    {
        await alertService.SendAlert(
            $"High error rate: {detector.GetMatchCount()} errors in last minute");
    }
    await Task.Delay(TimeSpan.FromSeconds(5));
}
```

---

## Example: Multi-Pattern Monitoring

```csharp
var sink = new SignalSink();

var errorDetector = new SignalAnomalyDetector(sink, "error.*", 10, TimeSpan.FromMinutes(1));
var timeoutDetector = new SignalAnomalyDetector(sink, "timeout.*", 5, TimeSpan.FromSeconds(30));
var apiDetector = new SignalAnomalyDetector(sink, "api.failure.*", 3, TimeSpan.FromSeconds(10));

// Check multiple anomaly conditions
if (errorDetector.IsAnomalous() || timeoutDetector.IsAnomalous())
    await TriggerCircuitBreaker();

if (apiDetector.IsAnomalous())
    await NotifyOnCall();
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Circuit breaker |
| [mostlylucid.ephemeral.patterns.signallogwatcher](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher) | Signal log watcher |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

MIT
