# Mostlylucid.Ephemeral.Logging

Provides adapters between `Microsoft.Extensions.Logging` and the signal world.

## Usage

- **Log → Signals**: attach `SignalLoggerProvider` to your `ILoggerFactory` (pass a shared `SignalSink`/`TypedSignalSink<SignalLogPayload>`), and logs auto-emit slugged signals with typed payloads.
- **Signals → Log**: plug `SignalToLoggerAdapter` into your signal sink to mirror signals back into `ILogger` with inferred levels.

Both directions keep the `SignalSink`/`SignalEvent` plumbing centralized while packaging the logging surface separately.
