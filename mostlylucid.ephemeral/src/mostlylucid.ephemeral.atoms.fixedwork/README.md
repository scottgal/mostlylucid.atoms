# Mostlylucid.Ephemeral.Atoms.FixedWork

A minimal atom wrapper around EphemeralWorkCoordinator for fixed-concurrency work pipelines.

## Features

- **Simple API**: Enqueue, drain, snapshot - nothing more
- **Stats tracking**: Get pending/active/completed/failed counts
- **Signal support**: Optional signal sink integration

## Installation

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.FixedWork
```

## Usage

```csharp
await using var atom = new FixedWorkAtom<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    maxConcurrency: 8);

// Enqueue work items
var id = await atom.EnqueueAsync(new WorkItem("data"));

// Check stats
var (pending, active, completed, failed) = atom.Stats();

// Get recent operation snapshots
var snapshots = atom.Snapshot();

// Drain when done
await atom.DrainAsync();
```

## API

| Method | Description |
|--------|-------------|
| `EnqueueAsync(T item)` | Enqueue work, returns operation ID |
| `DrainAsync()` | Complete and wait for all work |
| `Snapshot()` | Get recent operation snapshots |
| `Stats()` | Get (Pending, Active, Completed, Failed) counts |

## Dependencies

- Mostlylucid.Ephemeral (core library)

## License

MIT
