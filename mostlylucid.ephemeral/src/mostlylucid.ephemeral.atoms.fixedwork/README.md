# Mostlylucid.Ephemeral.Atoms.FixedWork

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.fixedwork.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.fixedwork)

Fixed-concurrency worker pool with stats. Minimal API wrapper around EphemeralWorkCoordinator.

```bash
dotnet add package mostlylucid.ephemeral.atoms.fixedwork
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.FixedWork;

await using var atom = new FixedWorkAtom<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    maxConcurrency: 4);

await atom.EnqueueAsync(item);

var (pending, active, completed, failed) = atom.Stats();
Console.WriteLine($"Completed: {completed}, Failed: {failed}");

await atom.DrainAsync();
```

---

## All Options

```csharp
new FixedWorkAtom<T>(
    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Max concurrent operations
    // Default: Environment.ProcessorCount
    maxConcurrency: 4,

    // Max operations retained in memory window
    // Default: 200
    maxTracked: 200,

    // Shared signal sink
    // Default: null (isolated)
    signals: sharedSink
)
```

---

## API Reference

```csharp
// Enqueue work item, returns operation ID
ValueTask<long> id = await atom.EnqueueAsync(item, ct);

// Stop accepting work and wait for completion
await atom.DrainAsync(ct);

// Get recent operations snapshot
IReadOnlyCollection<EphemeralOperationSnapshot> snapshot = atom.Snapshot();

// Get aggregate stats
var (pending, active, completed, failed) = atom.Stats();

// Dispose
await atom.DisposeAsync();
```

---

## Example: Processing with Stats

```csharp
await using var atom = new FixedWorkAtom<ApiRequest>(
    async (req, ct) =>
    {
        var response = await httpClient.SendAsync(req.Message, ct);
        response.EnsureSuccessStatusCode();
    },
    maxConcurrency: 8,
    maxTracked: 500);

// Enqueue batch
foreach (var request in requests)
    await atom.EnqueueAsync(request);

// Monitor progress
while (true)
{
    var (pending, active, completed, failed) = atom.Stats();
    Console.WriteLine($"Pending: {pending}, Active: {active}, Done: {completed}, Failed: {failed}");
    if (pending == 0 && active == 0) break;
    await Task.Delay(100);
}
```

---

## Related Packages

| Package                                                                                         | Description    |
|-------------------------------------------------------------------------------------------------|----------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                   | Core library   |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
