# Mostlylucid.Ephemeral.Logging

Provides adapters between `Microsoft.Extensions.Logging` and the signal world.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


## Log↔Signal features

- **Log → Signal**: `SignalLoggerProvider` converts `ILogger` events into slugged signals such as
  `log.error.orders.dbfailure`, carrying typed payloads with `EventId`, category, level, exception metadata, and
  captured scope properties so you can target specific errors by `EventId.Id`, `EventId.Name`, or exception type.
- **Signal → Log**: `SignalToLoggerAdapter` mirrors signals back into `ILogger` with inferred severity, payload, and
  message formatting so signals appear in standard telemetry sinks.
- **Customizable mapping**: `SignalLogHookOptions.MapSignal`/`MapPayload` let you control the emitted signal name or
  payload structure if you prefer different prefixes or labels.

```csharp
var sink = new SignalSink();
var typedSink = new TypedSignalSink<SignalLogPayload>(sink);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddProvider(new SignalLoggerProvider(typedSink, new SignalLogHookOptions
    {
        MinimumLevel = LogLevel.Warning,
        MapSignal = ctx => $"alert.{ctx.EventId.Name ?? ctx.EventId.Id}"
    }));
});
```

The typed payload gives you the `EventId`, exception type/message, and any captured scope values, so attribute jobs or
signal watchers can use those labels to drive downstream logic.

## Usage

- **Log → Signals**: attach `SignalLoggerProvider` to your `ILoggerFactory` (with a shared `SignalSink` or
  `TypedSignalSink<SignalLogPayload>`). The provider emits slugged `log.{level}.{category}.{event}` signals populated
  with typed payloads so attribute jobs, caches, or other listeners can react to logging events like any other signal.
- **Signals → Log**: plug `SignalToLoggerAdapter` into your signal sink to mirror signals back into `ILogger` with
  inferred log level, messages, and event ids.

Both directions keep the `SignalSink`/`SignalEvent` plumbing centralized while packaging the logging surface separately.

## Sample: log watcher pipeline

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
```

The `LogWatcherJobs` class (see `mostlylucid.ephemeral.attributes`) can then listen for `log.error.*` signals, raise
downstream escalation signals, and keep observability and remediation co-located with your signal-driven workflows. Use
`SignalToLoggerAdapter` when you want the resulting signal activity to re-appear in the standard logging pipeline as
well.