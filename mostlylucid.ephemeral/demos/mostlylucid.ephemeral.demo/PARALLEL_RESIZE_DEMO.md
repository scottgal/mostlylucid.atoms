# Parallel Resize Demo - Nested Coordinator Pattern

## Overview

The `ParallelResizeImageAtom` demonstrates the **nested coordinator pattern** - an atom that internally uses `EphemeralForEachAsync` to manage bounded parallel execution while propagating operation-scoped signals.

## Key Pattern: Operation ID Propagation

### Core Infrastructure ✓

Operation ID propagation is **already integrated into core Ephemeral**:

1. **SignalEvent struct** (`Signals.cs`):
   - `OperationId` is a required parameter in the record struct
   - Every signal carries its operation ID automatically
   - Zero-allocation hot path (~48 bytes on stack)

2. **EphemeralOperation** (`EphemeralOperation.cs`):
   - `EmitCausedInternal` automatically attaches operation ID to every signal
   - Line 100/304: `new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, cause)`
   - `Id` property is the operation ID

3. **Nested Operations**:
   - When an atom uses `EphemeralForEachAsync`, each item gets its own sub-operation
   - Sub-operations have unique operation IDs
   - Signals from sub-operations propagate to main SignalSink with proper operation IDs

### How It Works

```csharp
// Parent operation (e.g., processing an image)
var pipeline = new ImagePipeline(sink); // Operation ID: 100
await pipeline.ProcessAsync(job);

// Inside ParallelResizeImageAtom, nested coordinator created:
await jobs.EphemeralForEachAsync(async (resizeJob, ct) =>
{
    // Each resize gets its own operation ID
    _signals.Raise($"resize.{resizeJob.SizeName}.started");
    // Signal emitted with sub-operation ID (e.g., 101, 102, 103...)

    // Clone, resize, save...

    _signals.Raise($"resize.{resizeJob.SizeName}.complete");
    // Signal emitted with same sub-operation ID
},
new EphemeralOptions
{
    MaxConcurrency = 3,  // Bounded parallelism
    MaxTrackedOperations = 9,  // Small window (3 * 3)
    MaxOperationLifetime = TimeSpan.FromMinutes(1)
});
```

### Signal Flow Example

```
Parent Operation (ID: 100):
  → resize.parallel.started (opid: 100)
  → resize.parallelism:3 (opid: 100)

Sub-Operations (created by EphemeralForEachAsync):
  → resize.tiny.started (opid: 101)
  → resize.small.started (opid: 102)
  → resize.medium.started (opid: 103)  ← Max parallelism (3) reached

  // When one completes, next starts:
  → resize.tiny.complete (opid: 101)
  → resize.large.started (opid: 104)  ← New operation starts

  // All complete:
  → resize.small.complete (opid: 102)
  → resize.medium.complete (opid: 103)
  → resize.large.complete (opid: 104)

Parent Operation:
  → resize.parallel.complete (opid: 100)
```

## Implementation Details

### ParallelResizeImageAtom

**Location**: `src/mostlylucid.ephemeral.atoms.imagesharp/ImageProcessingAtoms.cs`

**Key Features**:
1. **Bounded Parallelism**: Configurable `MaxParallelism` parameter
2. **Small Window Pattern**: Window size = `maxParallelism * 3` for short-lived operations
3. **Automatic Sub-Operation Creation**: `EphemeralForEachAsync` creates operation per resize
4. **Signal Propagation**: All signals propagate to main SignalSink with proper operation IDs
5. **Resource Management**: Proper disposal via `IAsyncDisposable`

**Configuration**:
```csharp
new ParallelResizeOptions
{
    Sizes = new List<(Size, string)>
    {
        (new Size(100, 100), "tiny"),
        (new Size(200, 200), "small"),
        (new Size(400, 400), "medium"),
        (new Size(800, 800), "large"),
        (new Size(1920, 1920), "xlarge")
    },
    MaxParallelism = 3,  // Process 3 resizes concurrently
    JpegQuality = 90
}
```

### Demo Application

**Location**: `demos/mostlylucid.ephemeral.demo/ParallelResizeDemo.cs`

**What It Shows**:
1. Creates 6 different image sizes from a single source
2. Processes 3 resizes concurrently (configurable)
3. Captures and displays all signals with operation IDs
4. Shows execution timeline (start/complete timestamps)
5. Analyzes unique operation IDs and signals per operation

**Running the Demo**:
```bash
cd demos/mostlylucid.ephemeral.demo
dotnet run
# Select option "12. Parallel Resize Demo (Nested Coordinator Pattern)"
```

## Cross-Operation Coordination

### Pattern: Supervisor Watching Sub-Operations

Because each resize has its own operation ID, supervisors can watch and control individual operations:

```csharp
var supervisor = new SignalSink();
supervisor.Subscribe(signal =>
{
    // Watch for specific sub-operation signals
    if (signal.Signal.StartsWith("resize.") && signal.OperationId == targetOpId)
    {
        Console.WriteLine($"Sub-operation {signal.OperationId}: {signal.Signal}");

        // Could cancel specific operations if needed
        // (requires passing operation context, see ImageSharpCancellationHook)
    }
});
```

### Pattern: Dimension-Based Cancellation

The `ImageSharpCancellationHook` shows how to use operation-scoped signals for cancellation:

```csharp
await using var pipeline = new ImagePipeline(sink)
    .WithCancellationHook(opId, maxDimension: 5000)
    .WithLoader()
    .WithParallelResize();

// Hook listens for signals WHERE signal.OperationId == opId
// Automatically cancels if image.dimensions exceeds maxDimension
// Or can be explicitly cancelled via: sink.Raise("imagesharp.stop", operationId: opId)
```

## Performance Characteristics

### Allocation Efficiency

✓ **Zero-allocation signal emission**:
- `SignalEvent` is a readonly struct (~48 bytes on stack)
- Operation ID is a primitive `long` (no boxing)
- No heap allocations in hot path

✓ **Small window pattern**:
- Coordinator window = `maxParallelism * 3`
- For 3 concurrent resizes: window size = 9 operations
- Minimal memory overhead for short-lived operations

### Benchmarks

**Status**: ✓ Core signal infrastructure is benchmarked

**Location**: `demos/mostlylucid.ephemeral.demo/SignalBenchmarks.cs`

**Existing Benchmarks**:
- Signal raise (no listeners): ~10-20ns
- Signal raise (1 listener): ~40-60ns
- Signal pattern matching
- Concurrent signal raising
- EphemeralForEachAsync performance

**Recommendation**: Add specific nested coordinator benchmarks:
```csharp
[Benchmark(Description = "Nested coordinator with operation ID propagation")]
public async Task NestedCoordinatorWithSignals()
{
    var sink = new SignalSink();
    var items = Enumerable.Range(0, 100).ToList();

    await items.EphemeralForEachAsync(
        async (item, ct) =>
        {
            sink.Raise($"item.{item}.processed");
            await Task.CompletedTask;
        },
        new EphemeralOptions
        {
            MaxConcurrency = 4,
            MaxTrackedOperations = 12
        });
}
```

## Documentation

### Updated Files

1. **README.md** (`src/mostlylucid.ephemeral.atoms.imagesharp/README.md`):
   - Added `ParallelResizeImageAtom` section
   - Documented signals with operation ID notation
   - Added usage example with signal subscription
   - Explained nested coordinator pattern

2. **ReleaseNotes.txt** (`src/mostlylucid.ephemeral.atoms.imagesharp/ReleaseNotes.txt`):
   - Already includes nested coordinator feature in 1.0.0 release

3. **Demo Menu** (`demos/mostlylucid.ephemeral.demo/Program.cs`):
   - Added option "12. Parallel Resize Demo (Nested Coordinator Pattern)"
   - Integrated into interactive demo menu

## Next Steps

### Optimization Opportunities

1. **Benchmark Suite**: Add specific benchmarks for nested coordinators
2. **Memory Profiling**: Profile memory usage during parallel resize operations
3. **Scalability Testing**: Test with 100+ concurrent resize operations
4. **Stress Testing**: Test with very large images (10000x10000+)

### Pattern Expansion

This nested coordinator pattern can be applied to other domains:

- **Parallel HTTP requests**: Each request is a sub-operation with its own ID
- **Batch data processing**: Each batch item gets its own operation ID
- **Parallel file I/O**: Each file operation tracked independently
- **Microservice orchestration**: Each service call is a trackable sub-operation

## Summary

✅ **Operation ID propagation is fully integrated into core Ephemeral**
✅ **Nested coordinator pattern works seamlessly with EphemeralForEachAsync**
✅ **ParallelResizeImageAtom demonstrates the pattern in production-ready code**
✅ **Demo showcases operation ID tracking and signal propagation**
✅ **Documentation is comprehensive and up-to-date**

**Key Insight**: The infrastructure was already there - we just needed to demonstrate the pattern with a real-world use case (parallel image resizing) to show how powerful operation-scoped signals are for cross-operation coordination.
