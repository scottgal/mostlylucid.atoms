# Mostlylucid.Ephemeral.Attributes

Declaratively register signal-driven jobs using attributes on instance methods.

## Getting started

1. Annotate your job methods with `[EphemeralJob("signal.pattern")]`. Supported signatures:
   ```csharp
   [EphemeralJob("orders.process")]
   public async Task ProcessOrdersAsync(CancellationToken ct, SignalEvent signal) { ... }
   ```
   Methods may also omit arguments or take only `CancellationToken` or only `SignalEvent`.

2. Create a runner that scans your handlers and enqueues matching methods whenever the signal fires:
   ```csharp
   var signals = new SignalSink();
   var runner = new EphemeralSignalJobRunner(signals, new[] { new OrderJobs() });
   ```
   The runner enqueues jobs on an internal `EphemeralWorkCoordinator` (limited to the configured options) whenever the incoming signal matches the attribute pattern.

3. Emit signals with `signals.Raise("orders.process", key: "order-42")` from anywhere in your system to trigger the annotated jobs.

The package ships as `mostlylucid.ephemeral.attributes` and depends on `mostlylucid.ephemeral`.
