# Mostlylucid.Ephemeral.Patterns.SignalLogWatcher

Watches a SignalSink for matching signals and triggers callbacks. Useful for error monitoring and alerting.

## Usage

```csharp
var watcher = new SignalLogWatcher(
    signalSink,
    evt => Console.WriteLine($"Error: {evt.Signal}"),
    pattern: "error.*",
    pollInterval: TimeSpan.FromMilliseconds(100));

// When any signal matching "error.*" appears, the callback fires
// Automatically deduplicates to avoid repeated callbacks for same signal
```

## License

MIT
