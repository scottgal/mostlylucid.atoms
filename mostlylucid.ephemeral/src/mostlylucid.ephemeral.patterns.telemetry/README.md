# Mostlylucid.Ephemeral.Patterns.Telemetry

Async signal handler for telemetry integration with non-blocking I/O.

## Usage

```csharp
var handler = new TelemetrySignalHandler(telemetryClient);

// Wire up to coordinator signals
var options = new EphemeralOptions
{
    OnSignal = evt => handler.OnSignal(evt)
};

// Signals are processed in background, never blocking the main work
```

## License

MIT
