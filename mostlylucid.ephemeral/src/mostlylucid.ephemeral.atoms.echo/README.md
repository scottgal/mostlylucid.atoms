# Mostlylucid.Ephemeral.Atoms.Echo

Type-aware echoes for the important operations that survive just long enough to tell their story.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


This atom watches the same operation window that the coordinators expose, listens for typed signals (via
`TypedSignalSink<TPayload>`), and when the coordinator trims an operation it builds a compact
`OperationEchoEntry<TPayload>` that you can persist through an `OperationEchoAtom<TPayload>` or inspect in-process. It
is especially handy for attribute-driven jobs because they already run inside coordinators and can raise typed signals
with payloads describing their most critical state.

## Sample: capture the final state of attribute jobs

```csharp
var signalSink = new SignalSink();
var typedSink = new TypedSignalSink<EchoPayload>(signalSink);
var echoAtom = new OperationEchoAtom<EchoPayload>(async echo => await echoStore.AppendAsync(echo));

await using var coordinator = new EphemeralWorkCoordinator<Order>(
    async (order, ct) =>
    {
        // work...
        typedSink.Raise("echo.capture", new EchoPayload
        {
            OrderId = order.Id,
            Status = "persisting key artifacts"
        }, key: order.CustomerId);
    });

using var maker = coordinator.EnableOperationEchoing(
    typedSink,
    echoAtom,
    new OperationEchoMakerOptions<EchoPayload>
    {
        ActivationSignalPattern = "echo.capture",
        CaptureSignalPattern = "echo.*",
        MaxTrackedOperations = 128
    });
```

The atom keeps the echo window bounded (`MaxTrackedOperations`) and self-cleans stale captures (`MaxCaptureAge`).
Because the typed sink reuses the shared `SignalSink`, any other listeners still see the untyped `SignalEvent`.

## Attribute-friendly wiring

Decorated jobs can signal their own critical payloads:

```csharp
[EphemeralJob("orders.acknowledge")]
public Task OnAckAsync(SignalEvent evt)
{
    var payload = new EchoPayload
    {
        OrderId = evt.Key,
        Message = "ready for hand-off"
    };

    typedSink.Raise("echo.capture", payload, key: evt.Key);
    return Task.CompletedTask;
}
```

Because `TypedSignalSink<TPayload>` raises both typed and untyped events, the echo maker captures the payload while the
rest of your signal pipeline keeps running unchanged.

## Configuration tips

- `ActivationSignalPattern` lets you mark the point when an operation becomes echo-worthy (`"echo.capture"` in the
  sample). When set, the maker only captures signals after that activation signal (default is `null` to capture
  eagerly).
- `CaptureSignalPattern` and `CapturePredicate` let you filter which signals become part of the echo so you can ignore
  noise.
- `CaptureActivationSignal` controls whether the activation signal itself is included in the echo.
- `MaxTrackedOperations` / `MaxCaptureAge` keep the working set bounded so the echo maker doesn’t leak memory when
  collectors or consumers are slow.

Use `OperationEchoAtom<TPayload>` and `EnableOperationEchoing(...)` to persist echoes, replay them during diagnostics,
or trigger downstream recovery logic.