# Mostlylucid.Ephemeral.Patterns.AnomalyDetector

Moving-window anomaly detection based on signal pattern thresholds.

## Usage

```csharp
var detector = new SignalAnomalyDetector(
    signalSink,
    pattern: "error.*",
    threshold: 10,
    window: TimeSpan.FromSeconds(30));

if (detector.IsAnomalous())
{
    Console.WriteLine("Too many errors in the last 30 seconds!");
}
```

## License

MIT
