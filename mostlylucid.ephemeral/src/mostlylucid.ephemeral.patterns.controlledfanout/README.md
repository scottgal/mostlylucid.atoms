# Mostlylucid.Ephemeral.Patterns.ControlledFanOut

Controlled fan-out pattern: a global gate bounds total concurrency while per-key ordering is preserved.

## Use Case

Prevents runaway task spawning when many keys spike simultaneously. Each key maintains order, but total system parallelism is bounded.

## Usage

```csharp
var fanout = new ControlledFanOut<string, Message>(
    msg => msg.UserId,
    async (msg, ct) => await ProcessAsync(msg, ct),
    maxGlobalConcurrency: 16,
    perKeyConcurrency: 1);

await fanout.EnqueueAsync(message);
await fanout.DrainAsync();
```

## License

MIT
