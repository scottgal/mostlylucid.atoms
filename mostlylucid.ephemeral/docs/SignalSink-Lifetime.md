# SignalSink Lifetime and Multi-Coordinator Usage

## Overview

`SignalSink` is a **shared, long-lived signal repository** that acts as a global event bus for ephemeral operations. Unlike coordinators (which manage work execution), a sink is purely observational - it collects, stores, and makes signals queryable across multiple coordinators.

This document covers:
- SignalSink lifetime management
- Sharing sinks across coordinators
- Memory management and cleanup
- Thread-safety guarantees
- Common patterns and anti-patterns

---

## SignalSink Fundamentals

### What is a SignalSink?

```csharp
public sealed class SignalSink
{
    public SignalSink(int maxCapacity = 1000, TimeSpan? maxAge = null)
}
```

A `SignalSink`:
- Stores `SignalEvent` structs in a bounded `ConcurrentQueue<SignalEvent>`
- Auto-cleans based on **capacity** (size-based eviction) and **age** (time-based eviction)
- Provides push-based notification via `Subscribe()` and `SignalRaised` event
- Provides pull-based querying via `Sense()`, `Detect()`, `GetOpSignals()`
- Is **thread-safe** for all operations
- Has **no Dispose pattern** - cleanup is automatic

### Key Characteristics

| Property | Behavior |
|----------|----------|
| **Lifetime** | Lives as long as you hold a reference - no explicit disposal needed |
| **Thread-Safety** | Fully concurrent - safe to share across threads and coordinators |
| **Memory Model** | Bounded sliding window with automatic cleanup |
| **Cleanup** | Periodic (every ~1024 raises) with limited scan budget |
| **Ownership** | Shared reference - multiple coordinators can reference the same sink |

---

## SignalSink Lifetime

### Creation and Scope

```csharp
// ✅ Application-scoped sink (lives for app lifetime)
public class MyService
{
    private readonly SignalSink _globalSink = new SignalSink(
        maxCapacity: 5000,
        maxAge: TimeSpan.FromMinutes(5)
    );

    // Multiple coordinators can share this sink
}

// ✅ Request-scoped sink (lives for request duration)
public async Task HandleRequest(HttpContext ctx)
{
    var requestSink = new SignalSink(maxCapacity: 100, maxAge: TimeSpan.FromSeconds(30));

    // Use with coordinators processing this request
    await using var coordinator = new EphemeralWorkCoordinator<Task>(
        async (task, ct) => await task,
        new EphemeralOptions { Signals = requestSink }
    );

    // Sink goes out of scope when method exits
    // No explicit cleanup needed - GC handles it
}
```

### No Disposal Required

Unlike coordinators (which implement `IAsyncDisposable`), `SignalSink` has no dispose pattern:

```csharp
// SignalSink does NOT implement IDisposable or IAsyncDisposable
var sink = new SignalSink();

// ❌ NO NEED FOR:
// await sink.DisposeAsync();  // Doesn't exist
// sink.Dispose();             // Doesn't exist

// ✅ Just let it go out of scope
// Cleanup happens automatically via periodic maintenance
```

**Why?** SignalSink is a passive data structure with no background threads or unmanaged resources. The `ConcurrentQueue` is managed memory that GC will collect.

### Memory Management

SignalSink manages memory through **two cleanup mechanisms**:

#### 1. Size-Based Cleanup (Capacity)

```csharp
var sink = new SignalSink(maxCapacity: 1000);

// When signal count exceeds capacity, oldest signals are removed
// Cleanup runs periodically (every ~1024 raises) with budget of 1000 items
```

From `Signals.cs:590-600`:
```csharp
private void Cleanup()
{
    var cutoff = DateTimeOffset.UtcNow - MaxAge;
    var maxCapacity = MaxCapacity;

    // Size-based - limit iterations to prevent unbounded cleanup
    var removed = 0;
    while (_window.Count > maxCapacity && removed < 1000 && _window.TryDequeue(out _))
    {
        removed++;
    }
    // ...
}
```

#### 2. Age-Based Cleanup (Time)

```csharp
var sink = new SignalSink(
    maxCapacity: 5000,
    maxAge: TimeSpan.FromMinutes(1)  // Signals older than 1 minute are evicted
);
```

**Cleanup Trigger:**
- Runs every ~1024 signal raises (see `Signals.cs:418`)
- Limited to processing 1000 items per cleanup pass (prevents hangs)
- Not guaranteed to remove ALL expired items immediately (eventual consistency)

---

## Sharing SignalSink Across Coordinators

### The Canonical Pattern

Multiple coordinators can (and often should) share a single sink:

```csharp
// One sink, multiple coordinators
var sink = new SignalSink(maxCapacity: 10_000, maxAge: TimeSpan.FromMinutes(5));

// Coordinator 1: Uploads
await using var uploadCoordinator = new EphemeralWorkCoordinator<UploadJob>(
    async (job, ct) =>
    {
        // ... upload logic ...
        op.Emit("upload.complete");
    },
    new EphemeralOptions { Signals = sink }
);

// Coordinator 2: Transcoding
await using var transcodeCoordinator = new EphemeralWorkCoordinator<TranscodeJob>(
    async (job, ct) =>
    {
        // Listen for upload.complete from OTHER coordinator
        if (sink.Detect("upload.complete"))
        {
            op.Emit("transcode.started");
            // ... transcode logic ...
        }
    },
    new EphemeralOptions { Signals = sink }
);

// Both coordinators emit signals to the SAME sink
// Both can query the SAME signal history
```

### Why Share Sinks?

1. **Cross-Coordinator Coordination**
   One coordinator can react to signals from another:
   ```csharp
   // Coordinator A raises "upload.complete"
   // Coordinator B detects it and starts transcoding
   if (sink.Detect("upload.complete"))
       await StartTranscode();
   ```

2. **Unified Signal History**
   Query all signals across your system in one place:
   ```csharp
   var allErrors = sink.Sense(s => s.Signal.StartsWith("error."));
   ```

3. **Centralized Monitoring**
   One subscription sees signals from all coordinators:
   ```csharp
   sink.Subscribe(signal =>
   {
       // Receives signals from ALL coordinators using this sink
       _logger.LogInformation("Signal: {Signal} from Op {Id}",
           signal.Signal, signal.OperationId);
   });
   ```

### Lifetime Rules for Shared Sinks

```csharp
// ✅ GOOD: Sink outlives coordinators
var sink = new SignalSink();

await using var coord1 = new EphemeralWorkCoordinator<T>(body1, new() { Signals = sink });
await using var coord2 = new EphemeralWorkCoordinator<T>(body2, new() { Signals = sink });

// Coordinators can be disposed independently
await coord1.DisposeAsync();  // coord2 still using sink - FINE
await coord2.DisposeAsync();  // sink still lives - FINE

// Sink survives until GC collects it


// ❌ BAD: Coordinator outlives sink (edge case, still safe but wasteful)
var coord = new EphemeralWorkCoordinator<T>(body, new() { Signals = null });
{
    var sink = new SignalSink();
    coord.Options.Signals = sink;  // Can't actually do this - options are readonly!
    // This anti-pattern is prevented by the API design
}
// If it were possible, coord would reference a dead sink
```

**Key Point:** Since `EphemeralOptions.Signals` is set at construction time via `init`, you **cannot** accidentally orphan a coordinator by disposing its sink early.

---

## Signal Propagation Across Coordinators

### How Signals Flow

```csharp
var sink = new SignalSink();

var options1 = new EphemeralOptions
{
    Signals = sink,
    OnSignal = evt => Console.WriteLine($"Coord1 saw: {evt.Signal}")
};

var options2 = new EphemeralOptions
{
    Signals = sink,
    OnSignal = evt => Console.WriteLine($"Coord2 saw: {evt.Signal}")
};

await using var coord1 = new EphemeralWorkCoordinator<int>(
    async (item, ct) =>
    {
        op.Emit("coord1.started");
        await Task.Delay(100);
        op.Emit("coord1.complete");
    },
    options1
);

await using var coord2 = new EphemeralWorkCoordinator<int>(
    async (item, ct) =>
    {
        // Can see coord1's signals!
        if (sink.Detect("coord1.complete"))
        {
            op.Emit("coord2.reaction");
        }
    },
    options2
);
```

**What happens:**
1. Coord1 emits "coord1.started" → sent to `sink`
2. `sink` fires `SignalRaised` event → both `OnSignal` callbacks see it
3. `sink` stores signal in window (queryable by both coordinators)
4. Coord2 queries `sink.Detect("coord1.complete")` → finds it
5. Coord2 emits "coord2.reaction" → cycle continues

### Operation ID Isolation

Even though coordinators share a sink, **operation IDs remain unique**:

```csharp
var sink = new SignalSink();

// All signals have unique operation IDs from EphemeralIdGenerator
sink.Subscribe(signal =>
{
    Console.WriteLine($"Op {signal.OperationId}: {signal.Signal}");
});

// Coord1 operations: IDs 101, 102, 103...
// Coord2 operations: IDs 201, 202, 203...
// No collisions - IDs are globally unique via XxHash64 + Interlocked counter
```

From `EphemeralIdGenerator.cs`:
```csharp
public static long NextId()
{
    return Interlocked.Increment(ref _counter);  // Atomic global counter
}
```

---

## Thread Safety

SignalSink is **fully thread-safe** for all operations:

```csharp
var sink = new SignalSink();

// ✅ Safe: Concurrent raises from multiple threads
Parallel.For(0, 1000, i =>
{
    sink.Raise($"signal.{i}");  // Thread-safe
});

// ✅ Safe: Concurrent queries while raising
var task1 = Task.Run(() => sink.Sense());
var task2 = Task.Run(() => sink.Detect("test"));
var task3 = Task.Run(() => sink.Raise("new.signal"));

await Task.WhenAll(task1, task2, task3);  // All safe
```

### Concurrency Guarantees

| Operation | Thread-Safety | Guarantee |
|-----------|---------------|-----------|
| `Raise()` | Lock-free | Signal always enqueued |
| `Sense()` | Lock-free | Snapshot at moment of call |
| `Detect()` | Lock-free | May miss signals added during scan |
| `Subscribe()` | Locked | Consistent listener array update |
| `Cleanup()` | Lock-free | Bounded iteration, may skip items |

**Important:** Cleanup is **eventually consistent** - signals may survive slightly past expiration during concurrent access.

---

## Practical Patterns

### Pattern 1: Application-Scoped Global Sink

```csharp
// Startup.cs or Program.cs
public class Program
{
    // ONE sink for entire application
    private static readonly SignalSink GlobalSink = new SignalSink(
        maxCapacity: 50_000,
        maxAge: TimeSpan.FromMinutes(10)
    );

    public static void Main()
    {
        var builder = WebApplication.CreateBuilder();

        // Register as singleton
        builder.Services.AddSingleton(GlobalSink);

        var app = builder.Build();
        app.Run();
    }
}

// Usage in services
public class UploadService
{
    private readonly SignalSink _sink;

    public UploadService(SignalSink sink)  // Injected singleton
    {
        _sink = sink;
    }

    public async Task ProcessUploads()
    {
        await using var coordinator = new EphemeralWorkCoordinator<Upload>(
            async (upload, ct) => { /* ... */ },
            new EphemeralOptions { Signals = _sink }  // Shared sink
        );
    }
}
```

### Pattern 2: Per-Request Isolated Sinks

```csharp
public class RequestHandler
{
    public async Task<IActionResult> Handle(HttpContext context)
    {
        // One sink per request - isolated signal space
        var requestSink = new SignalSink(
            maxCapacity: 100,
            maxAge: TimeSpan.FromSeconds(30)
        );

        // All coordinators in this request share the request sink
        await using var coord1 = new EphemeralWorkCoordinator<T>(body1,
            new() { Signals = requestSink });
        await using var coord2 = new EphemeralWorkCoordinator<T>(body2,
            new() { Signals = requestSink });

        // Process...

        // When method exits, sink goes out of scope
        // No signal leakage between requests
    }
}
```

### Pattern 3: Hierarchical Sinks (Advanced)

```csharp
// Global sink for cross-service signals
var globalSink = new SignalSink(maxCapacity: 10_000);

// Service-specific sinks for local signals
var uploadSink = new SignalSink(maxCapacity: 1_000);
var transcodeSink = new SignalSink(maxCapacity: 1_000);

// Coordinators can emit to MULTIPLE sinks via custom signal handlers
var uploadCoordinator = new EphemeralWorkCoordinator<Upload>(
    async (upload, ct) =>
    {
        op.Emit("upload.started");  // Goes to uploadSink (via options)

        // Manually emit to global sink for cross-service visibility
        globalSink.Raise("global.upload.started", key: upload.Id.ToString());
    },
    new EphemeralOptions
    {
        Signals = uploadSink,
        OnSignal = evt => globalSink.Raise(evt)  // Mirror to global
    }
);
```

### Pattern 4: SignalSink with Subscribe for Monitoring

```csharp
var sink = new SignalSink();

// Live monitoring subscription
var subscription = sink.Subscribe(signal =>
{
    if (signal.Signal.StartsWith("error."))
    {
        _logger.LogError("Error signal: {Signal} from Op {Id}",
            signal.Signal, signal.OperationId);

        // Could trigger alerts, metrics, etc.
        _metrics.IncrementCounter("ephemeral.errors");
    }
});

// Coordinators use sink...
await using var coord = new EphemeralWorkCoordinator<T>(body,
    new() { Signals = sink });

// Later: Stop monitoring
subscription.Dispose();  // Unsubscribes
```

---

## Memory Characteristics

### Capacity Planning

```csharp
// Example: 10,000 signals × ~48 bytes per SignalEvent = ~480 KB
var sink = new SignalSink(maxCapacity: 10_000);

// SignalEvent is a readonly struct (~48 bytes on 64-bit):
// - string Signal (8 bytes reference)
// - long OperationId (8 bytes)
// - string? Key (8 bytes reference, nullable)
// - DateTimeOffset Timestamp (16 bytes)
// - SignalPropagation? Propagation (8 bytes reference, nullable)
```

### Cleanup Budget

From `Signals.cs:590-614`:
```csharp
private void Cleanup()
{
    // Size-based: Remove up to 1000 items per pass
    var removed = 0;
    while (_window.Count > maxCapacity && removed < 1000 && _window.TryDequeue(out _))
    {
        removed++;
    }

    // Age-based: Remove up to 1000 expired items per pass
    removed = 0;
    while (removed < 1000 && _window.TryDequeue(out var item))
    {
        if (item.Timestamp >= cutoff)
        {
            break;  // Stop when we hit non-expired signal
        }
        removed++;
    }
}
```

**Key Points:**
- Cleanup is **bounded** - max 2000 items removed per pass
- Cleanup runs every ~1024 raises (see `Signals.cs:418`)
- **Not all expired signals are guaranteed to be removed immediately**
- This prevents cleanup from blocking signal emission

### UpdateWindowSize for Dynamic Tuning

```csharp
var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));

// Later: Adjust capacity/age at runtime
sink.UpdateWindowSize(
    maxCapacity: 5000,
    maxAge: TimeSpan.FromMinutes(5)
);

Console.WriteLine($"New capacity: {sink.MaxCapacity}");  // 5000
Console.WriteLine($"New age: {sink.MaxAge}");            // 00:05:00
```

---

## Anti-Patterns

### ❌ Anti-Pattern 1: One Sink Per Coordinator

```csharp
// DON'T: Create separate sinks when coordinators should communicate
var coord1 = new EphemeralWorkCoordinator<T>(body1,
    new() { Signals = new SignalSink() });  // Isolated sink

var coord2 = new EphemeralWorkCoordinator<T>(body2,
    new() { Signals = new SignalSink() });  // Different sink

// Problem: coord1 and coord2 can't see each other's signals!
```

**Fix:** Share a sink when coordinators need to coordinate:
```csharp
var sink = new SignalSink();
var coord1 = new EphemeralWorkCoordinator<T>(body1, new() { Signals = sink });
var coord2 = new EphemeralWorkCoordinator<T>(body2, new() { Signals = sink });
```

### ❌ Anti-Pattern 2: Unbounded Sink

```csharp
// DON'T: Set huge capacity without age limit
var sink = new SignalSink(
    maxCapacity: int.MaxValue,  // Unbounded!
    maxAge: null                // No age cleanup!
);

// Problem: Memory grows without bound if signals keep arriving
```

**Fix:** Always set reasonable bounds:
```csharp
var sink = new SignalSink(
    maxCapacity: 10_000,               // Reasonable limit
    maxAge: TimeSpan.FromMinutes(5)    // Automatic expiration
);
```

### ❌ Anti-Pattern 3: Assuming Immediate Cleanup

```csharp
var sink = new SignalSink(maxCapacity: 10, maxAge: TimeSpan.FromMilliseconds(10));

sink.Raise("test");
await Task.Delay(20);  // Wait for expiration

Assert.Equal(0, sink.Count);  // ❌ MAY FAIL - cleanup is eventual!
```

**Fix:** Understand cleanup is periodic and bounded:
```csharp
// Trigger cleanup by raising more signals
for (int i = 0; i < 1100; i++)  // Force cleanup trigger
{
    sink.Raise($"trigger.{i}");
}

// Now expired signals are more likely to be cleaned
// But still not guaranteed - cleanup has budget limits
```

### ❌ Anti-Pattern 4: Relying on Signal Ordering Across Operations

```csharp
// DON'T: Assume signals from different operations arrive in order
sink.Raise(new SignalEvent("a", opId: 1, null, DateTimeOffset.UtcNow));
sink.Raise(new SignalEvent("b", opId: 2, null, DateTimeOffset.UtcNow));

var signals = sink.Sense();
// ❌ Can't assume signals[0].Signal == "a" - concurrent raises may reorder
```

**Fix:** Use timestamps or propagation chains for causality:
```csharp
var signals = sink.Sense().OrderBy(s => s.Timestamp).ToList();
// Now ordered by time
```

---

## Querying Signals Across Coordinators

### Get Signals From Specific Operation

```csharp
var sink = new SignalSink();

// Coordinator 1 emits signals with operation ID 42
var coord1 = new EphemeralWorkCoordinator<int>(
    async (item, ct) =>
    {
        op.Emit("coord1.started");
        op.Emit("coord1.processing");
        op.Emit("coord1.complete");
    },
    new() { Signals = sink }
);

await coord1.EnqueueAsync(1);

// Query signals from operation 42
var opSignals = sink.GetOpSignals(42);  // Returns all signals from that operation

// Get summary
var summary = sink.GetOp(42);
if (summary != null)
{
    Console.WriteLine($"Operation {summary.OperationId}:");
    Console.WriteLine($"  Key: {summary.Key}");
    Console.WriteLine($"  Duration: {summary.Duration}");
    Console.WriteLine($"  Signals: {summary.SignalCount}");
    foreach (var signal in summary.Signals)
    {
        Console.WriteLine($"    - {signal.Signal} at {signal.Timestamp}");
    }
}
```

### Pattern Matching Across All Operations

```csharp
// Find all error signals from any coordinator/operation
var errorSignals = sink.Sense(s => s.Signal.StartsWith("error."));

// Group by operation ID
var errorsByOperation = errorSignals
    .GroupBy(s => s.OperationId)
    .ToList();

foreach (var group in errorsByOperation)
{
    Console.WriteLine($"Operation {group.Key} had {group.Count()} errors:");
    foreach (var signal in group)
    {
        Console.WriteLine($"  - {signal.Signal}");
    }
}
```

---

## DI Integration

### Singleton Sink (Recommended)

```csharp
// Program.cs
builder.Services.AddSingleton(new SignalSink(
    maxCapacity: 50_000,
    maxAge: TimeSpan.FromMinutes(10)
));

// Service
public class MyService
{
    private readonly SignalSink _sink;

    public MyService(SignalSink sink)  // Injected
    {
        _sink = sink;
    }

    public async Task DoWork()
    {
        await using var coordinator = new EphemeralWorkCoordinator<T>(
            async (item, ct) => { /* ... */ },
            new EphemeralOptions { Signals = _sink }
        );
    }
}
```

### Scoped Sink (Per-Request)

```csharp
// Program.cs
builder.Services.AddScoped<SignalSink>(sp => new SignalSink(
    maxCapacity: 100,
    maxAge: TimeSpan.FromSeconds(30)
));

// Controller/Service
public class MyController : ControllerBase
{
    private readonly SignalSink _requestSink;

    public MyController(SignalSink requestSink)  // Scoped per request
    {
        _requestSink = requestSink;
    }

    public async Task<IActionResult> Process()
    {
        // All coordinators in this request share the scoped sink
        await using var coord = new EphemeralWorkCoordinator<T>(
            async (item, ct) => { /* ... */ },
            new EphemeralOptions { Signals = _requestSink }
        );

        // ...
    }
}
```

---

## Performance Characteristics

| Operation | Complexity | Notes |
|-----------|------------|-------|
| `Raise()` | O(1) | Lock-free enqueue + bounded listener invocation |
| `Sense()` | O(n) | Snapshot of entire window |
| `Sense(predicate)` | O(n) | Linear scan with filtering |
| `Detect()` | O(n) worst, O(1) best | Short-circuits on first match |
| `Subscribe()` | O(1) | Array copy under lock |
| `Cleanup()` | O(1) amortized | Bounded to 2000 items/pass, runs every ~1024 raises |

**From `ParallelResizeDemo.cs:299`:**
```csharp
// Window sized for ~2 batches: 80 images × 3 sizes × 6 signals × 2 batches = ~2880 signals
var sink = new SignalSink(maxCapacity: 3000, maxAge: TimeSpan.FromSeconds(30));
```

Capacity planning: `maxCapacity` should be sized for **2-3× your active signal window** to avoid excessive cleanup overhead.

---

## Summary

### Key Takeaways

1. **SignalSink has no disposal** - it's a pure managed data structure
2. **Fully thread-safe** - share across coordinators and threads freely
3. **Bounded memory** - cleanup runs periodically with budget limits
4. **Eventual consistency** - cleanup is best-effort, not immediate
5. **Singleton or scoped** - depends on your coordination needs
6. **Cross-coordinator visibility** - shared sink = shared signal space

### When to Share a Sink

**Share when:**
- Coordinators need to react to each other's signals
- You want unified signal history across subsystems
- Monitoring/logging needs to see all signals in one place

**Don't share when:**
- Coordinators are completely independent
- You want signal isolation (e.g., per-tenant, per-request)
- Different coordinators have vastly different signal lifetimes

### Lifetime Rules

```
Application Lifetime > Sink Lifetime ≥ Coordinator Lifetime
```

- Application-scoped sink: Lives for entire app (singleton)
- Request-scoped sink: Lives for request duration (scoped DI)
- Coordinator-scoped sink: Lives as long as coordinator (local variable)

No explicit cleanup needed for any scope - GC handles it when references are gone.

---

## Further Reading

- [Signals.cs source](../src/mostlylucid.ephemeral/Signals/Signals.cs) - Full implementation
- [SignalSinkTests.cs](../tests/mostlylucid.ephemeral.tests/SignalSinkTests.cs) - Test coverage
- [ParallelResizeDemo.cs](../demos/mostlylucid.ephemeral.demo/ParallelResizeDemo.cs) - Real-world usage
- [ControlledFanOut.cs](../src/mostlylucid.ephemeral.patterns.controlledfanout/ControlledFanOut.cs) - Multi-coordinator pattern
