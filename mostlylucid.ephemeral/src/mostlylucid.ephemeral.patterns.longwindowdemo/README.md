# Mostlylucid.Ephemeral.Patterns.LongWindowDemo

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.longwindowdemo.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.longwindowdemo)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Demonstrates configurable window sizes - from tiny to thousands of tracked operations.

```bash
dotnet add package mostlylucid.ephemeral.patterns.longwindowdemo
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.LongWindowDemo;

// Small window - minimal memory footprint
var small = await LongWindowDemo.RunAsync(
    totalItems: 10000,
    windowSize: 50);
Console.WriteLine($"Tracked: {small.TrackedCount} of {small.TotalItems}");

// Large window - track everything
var large = await LongWindowDemo.RunAsync(
    totalItems: 1000,
    windowSize: 2000);
Console.WriteLine($"Tracked: {large.TrackedCount} of {large.TotalItems}");
```

---

## All Options

```csharp
LongWindowDemo.RunAsync(
    // Total number of items to process
    totalItems: 1000,

    // Max operations to keep in memory
    windowSize: 200,

    // Optional delay per work item (ms)
    // Default: 0
    workDelayMs: 0,

    // Optional cancellation token
    ct: cancellationToken
)
```

---

## API Reference

```csharp
// Run the demo
Task<Result> LongWindowDemo.RunAsync(
    int totalItems,
    int windowSize,
    int workDelayMs = 0,
    CancellationToken ct = default);

// Result record
public readonly record struct Result(
    int WindowSize,      // Configured window size
    int TotalItems,      // Total items processed
    int TrackedCount);   // Items currently in snapshot
```

---

## How It Works

`MaxTrackedOperations` controls how many completed operations stay in memory:

```
Process 10,000 items with windowSize: 50
┌─────────────────────────────────────────────────────────────────┐
│ [1] [2] [3] ... [9950] [9951] [9952] ... [9999] [10000]        │
│                        └──────────────────────────────┘        │
│                         Only last 50 tracked in memory         │
└─────────────────────────────────────────────────────────────────┘
Result: TrackedCount = 50
```

Oldest completed operations are evicted when the limit is reached.

---

## Configuration Trade-offs

| Window Size  | Memory | Observability |
|--------------|--------|---------------|
| Small (50)   | Low    | Recent only   |
| Medium (500) | Medium | Good history  |
| Large (5000) | Higher | Full tracking |

---

## Example: Memory-Efficient Processing

```csharp
// Process millions of items with minimal memory
var result = await LongWindowDemo.RunAsync(
    totalItems: 1_000_000,
    windowSize: 100);

// Only 100 operations in memory at any time
Console.WriteLine($"Processed: {result.TotalItems:N0}");
Console.WriteLine($"Tracked: {result.TrackedCount}");
// Output:
// Processed: 1,000,000
// Tracked: 100
```

---

## Example: Full Observability

```csharp
// Track every operation for debugging
var result = await LongWindowDemo.RunAsync(
    totalItems: 500,
    windowSize: 1000,
    workDelayMs: 10);

// All 500 operations tracked (window larger than total)
Console.WriteLine($"Tracked: {result.TrackedCount} of {result.TotalItems}");
// Output:
// Tracked: 500 of 500
```

---

## Example: Custom Coordinator with Window

```csharp
// Apply the pattern to your own coordinator
var options = new EphemeralOptions
{
    MaxTrackedOperations = 200,  // Tune based on needs
    MaxConcurrency = 8
};

await using var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    ProcessWorkItemAsync,
    options);

// Process work
foreach (var item in items)
    await coordinator.EnqueueAsync(item);

coordinator.Complete();
await coordinator.DrainAsync();

// Snapshot shows last 200 operations max
var snapshot = coordinator.GetSnapshot();
Console.WriteLine($"Snapshot contains: {snapshot.Count} operations");
```

---

## Related Packages

| Package                                                                                         | Description    |
|-------------------------------------------------------------------------------------------------|----------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                   | Core library   |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)