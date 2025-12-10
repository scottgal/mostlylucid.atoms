# Mostlylucid.Ephemeral.Atoms.Volatile

Remove completed operations the moment they emit a kill signal so your window stays tiny and every operation is truly ephemeral.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Every `EphemeralWorkCoordinator` now implements `IOperationEvictor.TryKill(long)` so you can ask it to drop an operation immediately. `VolatileOperationAtom` hooks a shared `SignalSink`, listens for a configurable kill pattern (default `kill.*`), and evicts the matching operation as soon as its kill signal arrives. Combine with `EphemeralOptions.EnableOperationEcho` / `OperationEchoMaker` if you still need a trimmed copy of the final signal wave.

```csharp
var sink = new SignalSink();
await using var coordinator = new EphemeralWorkCoordinator<JobItem>(
    async (job, ct) => await ProcessQuick(job, ct),
    new EphemeralOptions
    {
        Signals = sink,
        MaxTrackedOperations = 32,
        EnableOperationEcho = true,
        OperationEchoRetention = TimeSpan.FromSeconds(30)
    });

using var volatileAtom = new VolatileOperationAtom(sink, coordinator);

await coordinator.EnqueueAsync(new JobItem("work-1"));
// inside your job: emitter.Emit("kill.work");
```

When the job raises `kill.work`, the atom finds the operation ID carried in the signal, calls `TryKill`, and the operation disappears instantly. Echos stay intact because the coordinator still raises `OperationFinalized`, which feeds the echo store. Use `OperationEchoAtom` / `OperationEchoMaker` to persist the echo via `*.echo.start` / `*.echo.end` signals before the kill finally removes the operation.