# Mostlylucid.Ephemeral.Patterns.SignalCoordinatedReads

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.signalcoordinatedreads.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalcoordinatedreads)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Signal-coordinated readers that pause during updates - quiesce reads without hard locks.

```bash
dotnet add package mostlylucid.ephemeral.patterns.signalcoordinatedreads
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalCoordinatedReads;

var result = await SignalCoordinatedReads.RunAsync(
    readCount: 100,
    updateCount: 5);

Console.WriteLine($"Reads: {result.ReadsCompleted}, Updates: {result.UpdatesCompleted}");
```

---

## All Options

```csharp
SignalCoordinatedReads.RunAsync(
    // Number of read operations to run
    // Default: 10
    readCount: 10,

    // Number of update operations to run
    // Default: 1
    updateCount: 1,

    // Optional cancellation token
    ct: cancellationToken
)
```

---

## API Reference

```csharp
// Run the coordinated read/update demo
Task<Result> SignalCoordinatedReads.RunAsync(
    int readCount = 10,
    int updateCount = 1,
    CancellationToken ct = default);

// Result record
public sealed record Result(
    int ReadsCompleted,
    int UpdatesCompleted,
    IReadOnlyList<string> Signals);
```

---

## How It Works

```
Readers defer on "update.in-progress" signal:

   Reader 1: [read] ─────────────────────────────────> [read]
   Reader 2: [read] ──────────> [defer...] ──────────> [read]
   Reader 3: [read] ──────────> [defer...] ──────────> [read]
                                    │
   Updater:        ═══[update.in-progress]═══[update.done]═══
```

Signals used:

- `update.in-progress` - Readers defer while this is present
- `update.done` - Update completed marker
- `read.waiting` - Reader is waiting for update to complete

---

## Use Cases

- Config reloads without blocking readers permanently
- Database migrations with graceful read pauses
- Cache invalidation coordination
- Schema updates with minimal read disruption

---

## Example: Config Reload Pattern

```csharp
var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromSeconds(5));

// Reader coordinator - defers on update signal
await using var readers = new EphemeralWorkCoordinator<ConfigRequest>(
    async (req, ct) =>
    {
        var config = await GetCurrentConfig(ct);
        await ProcessWithConfig(req, config, ct);
    },
    new EphemeralOptions
    {
        MaxConcurrency = 8,
        Signals = sink,
        DeferOnSignals = new HashSet<string> { "config.updating" },
        DeferCheckInterval = TimeSpan.FromMilliseconds(20),
        MaxDeferAttempts = 500
    });

// Updater - signals during update
await using var updater = new EphemeralWorkCoordinator<ConfigUpdate>(
    async (update, ct) =>
    {
        sink.Raise("config.updating");
        try
        {
            await ApplyConfigUpdate(update, ct);
        }
        finally
        {
            sink.Retract("config.updating");
            sink.Raise("config.updated");
        }
    },
    new EphemeralOptions { MaxConcurrency = 1, Signals = sink });
```

## Attribute-driven config reload

```csharp
[EphemeralJobs(SignalPrefix = "config", DefaultLane = "reader")]
public sealed class ConfigJobs
{
    private readonly SignalSink _signals;
    private readonly IConfigurationService _config;

    public ConfigJobs(SignalSink signals, IConfigurationService config)
    {
        _signals = signals;
        _config = config;
    }

    [EphemeralJob("reader", AwaitSignals = new[] { "config.updated" }, MaxConcurrency = 8)]
    public async Task ReaderAsync(ConfigRequest request, CancellationToken ct)
    {
        var config = await _config.LoadAsync(ct);
        await request.ProcessAsync(config, ct);
    }

    [EphemeralJob("reload", EmitOnStart = new[] { "config.updating" }, EmitOnComplete = new[] { "config.updated" }, MaxConcurrency = 1)]
    public Task ReloadAsync(ConfigUpdate update, CancellationToken ct)
        => _config.ApplyAsync(update, ct);
}

var sink = new SignalSink();
var jobs = new ConfigJobs(sink, configService);
await using var runner = new EphemeralSignalJobRunner(sink, new[] { jobs });

// Trigger reloads when needed
sink.Raise("config.reload");
```

`EphemeralSignalJobRunner` ties the attribute handlers to the signal stream, automatically sequencing readers after
updates via `AwaitSignals` and sharing the same `SignalSink` used for manual coordinators.

---

## Example: Database Migration

```csharp
var sink = new SignalSink();

// Queries defer during migration
await using var queries = new EphemeralWorkCoordinator<Query>(
    ExecuteQueryAsync,
    new EphemeralOptions
    {
        Signals = sink,
        DeferOnSignals = new HashSet<string> { "migration.*" }
    });

// Migration signals its phases
await using var migration = new EphemeralWorkCoordinator<Migration>(
    async (m, ct) =>
    {
        sink.Raise("migration.starting");
        await m.RunAsync(ct);
        sink.Raise("migration.complete");
        sink.Retract("migration.starting");
    },
    new EphemeralOptions { Signals = sink, MaxConcurrency = 1 });
```

---

## Configuration Details

The demo internally uses:

```csharp
// Reader options
new EphemeralOptions
{
    MaxConcurrency = 4,
    Signals = sink,
    DeferOnSignals = new HashSet<string> { "update.in-progress" },
    DeferCheckInterval = TimeSpan.FromMilliseconds(20),
    MaxDeferAttempts = 500
}

// Updater options
new EphemeralOptions
{
    MaxConcurrency = 1,
    Signals = sink
}
```

---

## Related Packages

| Package                                                                                                                   | Description          |
|---------------------------------------------------------------------------------------------------------------------------|----------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                             | Core library         |
| [mostlylucid.ephemeral.patterns.backpressure](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure) | Backpressure pattern |
| [mostlylucid.ephemeral.atoms.signalaware](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)         | Signal-aware atom    |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                           | All in one DLL       |

## License

Unlicense (public domain)