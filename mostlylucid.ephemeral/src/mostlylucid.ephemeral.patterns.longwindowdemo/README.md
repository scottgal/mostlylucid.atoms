# Mostlylucid.Ephemeral.Patterns.LongWindowDemo

Demonstrates configurable window sizes - from tiny to thousands of tracked operations.

## How It Works

- `MaxTrackedOperations` controls how many operations stay in memory
- Oldest completed operations are evicted when limit is reached
- Window can be tiny (memory-efficient) or large (full observability)

## Usage

```csharp
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

## Configuration Trade-offs

| Window Size | Memory | Observability |
|-------------|--------|---------------|
| Small (50)  | Low    | Recent only   |
| Medium (500)| Medium | Good history  |
| Large (5000)| Higher | Full tracking |

## License

MIT
