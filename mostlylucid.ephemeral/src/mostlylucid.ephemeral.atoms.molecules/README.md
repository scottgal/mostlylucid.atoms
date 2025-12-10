# Mostlylucid.Ephemeral.Atoms.Molecules

Reusable Molecule blueprints and atom-trigger helpers for coordinating multi-atom workflows.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


## Molecules

A molecule is a blueprint: signal → orchestrated atoms. Use `MoleculeBlueprintBuilder` to describe the atoms (steps) you
want to run, then feed the blueprint into `MoleculeRunner`. The runner listens for signals matching your trigger
pattern, creates a `MoleculeContext`, and executes each step in order while sharing the same `SignalSink` and service
provider. Once created you can still call `blueprint.AddAtom(...)` or `blueprint.RemoveAtoms(...)` to tweak the workflow
before the next trigger, enabling dynamic post-composition.

```csharp
var sink = new SignalSink();
var blueprint = new MoleculeBlueprintBuilder("OrderFulfillment", "order.placed")
    .AddAtom(async (ctx, ct) =>
    {
        await paymentCoordinator.EnqueueAsync(new Payment(ctx.TriggerSignal.Key!), ct);
        ctx.Raise("order.payment.complete", ctx.TriggerSignal.Key);
    })
    .AddAtom(async (ctx, ct) =>
    {
        await inventoryCoordinator.EnqueueAsync(new InventoryReservation(ctx.TriggerSignal.Key!), ct);
    })
    .Build();

await using var runner = new MoleculeRunner(sink, new[] { blueprint }, services);
sink.Raise("order.placed", key: "order-123");
```

You get events (`MoleculeStarted`, `MoleculeCompleted`, `MoleculeFailed`) to observe every workflow run.

## Atoms that trigger atoms

`AtomTrigger` listens for a pattern and invokes your callback with the signal. The callback can enqueue work on another
coordinator, start new atoms, or raise more signals.

```csharp
using var trigger = new AtomTrigger(sink, "order.payment.complete", async (signal, ct) =>
{
    await notificationCoordinator.EnqueueAsync(new Notification(signal.Key!), ct);
});
```

This keeps the entire orchestrable workflow inside the signal ecosystem without introducing additional wiring.