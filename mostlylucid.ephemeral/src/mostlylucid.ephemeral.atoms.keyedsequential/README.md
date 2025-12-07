# Mostlylucid.Ephemeral.Atoms.KeyedSequential

Per-key sequential execution atom. Ensures ordering per key while allowing global parallelism.

## Features

- **Per-key ordering**: Items with the same key are processed sequentially
- **Global parallelism**: Different keys can be processed in parallel
- **Fair scheduling**: Optional fair scheduling prevents hot keys from starving cold keys

## Usage

```csharp
await using var atom = new KeyedSequentialAtom<Message, string>(
    msg => msg.UserId,  // Key selector
    async (msg, ct) => await ProcessAsync(msg, ct),
    maxConcurrency: 8,
    perKeyConcurrency: 1,  // Sequential per user
    enableFairScheduling: true);

// Messages for the same user are processed in order
await atom.EnqueueAsync(new Message("user1", "Hello"));
await atom.EnqueueAsync(new Message("user1", "World"));  // Waits for Hello

// Different users can run in parallel
await atom.EnqueueAsync(new Message("user2", "Hi"));

await atom.DrainAsync();
```

## API

| Parameter | Description |
|-----------|-------------|
| `keySelector` | Function to extract key from item |
| `maxConcurrency` | Global max parallel operations |
| `perKeyConcurrency` | Max parallel operations per key (default: 1) |
| `enableFairScheduling` | Prevent hot keys from monopolizing |

## License

MIT
