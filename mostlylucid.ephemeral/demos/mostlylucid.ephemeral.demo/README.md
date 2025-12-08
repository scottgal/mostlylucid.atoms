# Ephemeral Signals Demo

Interactive demonstration of the **Ephemeral Signals** pattern using Spectre.Console.

## What is the Ephemeral Signals Pattern?

The Ephemeral Signals pattern is based on a simple philosophy:

> **"Hey, look at me!"**

Signals are **notifications**, not state carriers. The pattern is:
- **Signal provides context** (what happened)
- **Atom holds state** (current truth)
- **Listeners query atoms** (get authoritative data)

This creates a non-locking, "sure of value" system where state remains centralized and queryable.

## The Three Signal Models

### 1. Pure Notification (Default)

Signal carries **no data**—just an event name.

```csharp
sink.Raise("file.saved");
// Listener queries the atom:
var filename = fileAtom.GetCurrentFilename();
```

**When to use:** Domain events, state changes, most use cases

### 2. Context + Hint (Double-Safe)

Signal carries a **hint** for optimization, but listeners **verify** with atom.

```csharp
sink.Raise("order.placed:ORD-123"); // Hint for fast-path
// Fast-path uses hint, then verifies:
var actualOrderId = orderAtom.GetLastOrderId();
```

**When to use:** Performance optimization, but safety matters

### 3. Command (Exception)

Signal carries **imperative command** for infrastructure.

```csharp
sink.Raise("window.size.set:500"); // Command with payload
```

**When to use:** Infrastructure control, admin operations, debug commands
**Not for:** Domain events, business logic

## Running the Demos

### Prerequisites

- .NET 10.0 SDK
- Terminal with ANSI color support (Windows Terminal, VS Code terminal, etc.)

### Build and Run

```bash
cd mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo
dotnet run
```

### Available Demos

**Pattern Demonstrations:**
1. **Pure Notification Pattern** - File save simulation with state queries
2. **Context + Hint Pattern** - Order processing with double-safe optimization
3. **Command Pattern** - WindowSizeAtom infrastructure control (exception)
4. **Complex Multi-Step System** - Rate limiting pipeline with signal chains
5. **Signal Chain Demo** - Cascading atoms (A→B→C)

**Advanced Patterns:**
6. **Circuit Breaker Pattern** - Failure detection and automatic recovery
7. **Backpressure Demo** - Queue overflow protection via flow control
8. **Metrics & Monitoring** - Real-time statistics with live dashboard
9. **Dynamic Rate Adjustment** - Adaptive throttling based on system load
10. **Live Signal Viewer** - Real-time signal visualization with filtering

**Performance Analysis:**
- **BenchmarkDotNet** - Memory diagnostics, allocation tracking, GC pressure analysis

### Benchmark Mode

**IMPORTANT:** Benchmarks require Release mode for accurate results.

```bash
cd mostlylucid.ephemeral/demos/mostlylucid.ephemeral.demo

# Run in Release mode
dotnet run -c Release

# Or build and run the Release executable
dotnet build -c Release
cd bin/Release/net10.0
./mostlylucid.ephemeral.demo  # or .exe on Windows

# Then select "B. Run Benchmarks (BenchmarkDotNet)" from menu
```

If you try to run benchmarks in Debug mode, you'll see a helpful error message with instructions.

**Benchmarks include:**
- Signal raising (with/without listeners)
- Pattern matching performance
- Command parsing overhead
- Rate limiter acquire latency
- State query performance
- Window size atom commands
- Signal chain propagation
- Concurrent signal raising

Results show:
- Mean execution time
- Memory allocations (Gen 0/1/2 GC)
- Allocated bytes per operation
- Standard deviation and outliers

## Demo Scenarios

### 1. Pure Notification Pattern (File Save)

**Demonstrates:**
- Signal: `file.save` → `file.saved` (no payload)
- Multiple listeners query the same atom for different state
- Notification vs state separation

**Key Code:**
```csharp
await using var fileAtom = new TestAtom(
    sink,
    "FileAtom",
    listenSignals: new List<string> { "file.save" },
    signalResponses: new Dictionary<string, string>
    {
        { "file.save", "file.saved" }
    });

// Listener queries atom for state
var notificationListener = new Action<SignalEvent>(signal =>
{
    if (signal.Signal == "file.saved")
    {
        var count = fileAtom.GetProcessedCount();
        // Use state from atom, not from signal
    }
});
```

**Takeaway:** Signal is just a notification. State is queried from the atom.

---

### 2. Context + Hint Pattern (Order Processing)

**Demonstrates:**
- Signal: `order.placed:ORD-123` (hint included)
- Fast-path uses hint, but verifies with atom
- Double-safe pattern balances performance and safety

**Key Code:**
```csharp
var emailListener = new Action<SignalEvent>(signal =>
{
    if (signal.Signal.StartsWith("order.placed"))
    {
        // ✅ DOUBLE-SAFE: Use hint for fast-path, verify with atom
        var hintOrderId = signal.Signal.Split(':')[1];

        // Fast-path: use hint
        SendEmail(hintOrderId);

        // Always verify with atom for safety
        var actualCount = orderAtom.GetProcessedCount();
    }
});
```

**Takeaway:** Hint in signal for speed, atom query for truth.

---

### 3. Command Pattern (Window Size Control)

**Demonstrates:**
- Signal: `window.size.set:500` (imperative command)
- WindowSizeAtom adjusts SignalSink capacity dynamically
- Exception to the normal pattern (infrastructure only)

**Key Code:**
```csharp
await using var windowAtom = new WindowSizeAtom(sink);

sink.Raise("window.size.set:500");
// Capacity is now 500

sink.Raise("window.time.set:30s");
// Retention is now 30 seconds
```

**Takeaway:** Command pattern is for infrastructure only, not domain events.

---

### 4. Complex Multi-Step System (Rate Limiting Pipeline)

**Demonstrates:**
- Signal chains: `api.request` → `request.validated` → `request.processed` → `request.complete`
- Rate limiting with `RateLimitAtom` (1.5/s, burst 3)
- Dynamic window sizing
- Multiple atoms querying each other's state

**Key Code:**
```csharp
// Rate limiter controls throughput
await using var rateLimiter = new RateLimitAtom(sink, new RateLimitOptions
{
    InitialRatePerSecond = 1.5,
    Burst = 3
});

// Try to acquire rate limit lease
using var lease = await rateLimiter.AcquireAsync();

if (lease.IsAcquired)
{
    sink.Raise("api.request");
}
```

**Takeaway:** Complex pipelines can be built from simple signal-driven atoms.

---

### 5. Signal Chain Demo (Cascading Atoms)

**Demonstrates:**
- Atoms emitting signals that trigger other atoms
- Pipeline: `input` → `stepA.complete` → `stepB.complete` → `stepC.complete`
- Each atom processes and passes to next stage

**Key Code:**
```csharp
await using var atomA = new TestAtom(
    sink, "AtomA",
    listenSignals: new List<string> { "input" },
    signalResponses: new Dictionary<string, string> { { "input", "stepA.complete" } });

await using var atomB = new TestAtom(
    sink, "AtomB",
    listenSignals: new List<string> { "stepA.complete" },
    signalResponses: new Dictionary<string, string> { { "stepA.complete", "stepB.complete" } });

sink.Raise("input"); // Triggers chain: A → B → C
```

**Takeaway:** Atoms can form processing pipelines via signal chains.

---

### 6. Live Signal Viewer

**Demonstrates:**
- Real-time signal visualization
- ConsoleSignalLoggerAtom with filtering
- Color-coded log levels (error=red, warning=yellow, etc.)
- Signal window dump

**Key Code:**
```csharp
await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
{
    AutoOutput = true,        // Print signals as they arrive
    WindowSize = 200,         // Keep last 200 signals
    SampleRate = 1,           // Log every signal
    ExcludePatterns = new List<string> { "debug.*" } // Filter out debug
});

logger.DumpWindow(); // Print all signals in window
```

**Takeaway:** Signals can be observed and logged for debugging and monitoring.

## Components

### TestAtom

Simulated atom for demonstration purposes.

**Features:**
- Configurable signal listeners (glob pattern matching)
- Configurable signal responses (emit signals in response to received signals)
- Simulated processing delay (`Task.Delay`)
- State storage (processed count, last signal, processing history, busy flag)
- State query methods demonstrating the pattern

**Example:**
```csharp
await using var atom = new TestAtom(
    sink,
    name: "MyAtom",
    listenSignals: new List<string> { "input.*", "command.start" },
    signalResponses: new Dictionary<string, string>
    {
        { "input.*", "processing.started" },
        { "command.start", "processing.started" }
    },
    processingDelay: TimeSpan.FromMilliseconds(100));

// Query state
var count = atom.GetProcessedCount();
var lastSignal = atom.GetLastProcessedSignal();
var isBusy = atom.IsBusy();
```

### ConsoleSignalLoggerAtom

Captures signals and outputs to console with filtering and sampling.

**Features:**
- Auto-output to console
- Window size limiting
- Include/exclude pattern filtering (glob)
- Sample rate control
- Color-coded output by log level
- ILogger integration (optional)
- Window dump to table

**Example:**
```csharp
await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
{
    AutoOutput = true,
    WindowSize = 100,
    SampleRate = 2, // Log every 2nd signal
    IncludePatterns = new List<string> { "error.*", "warning.*" },
    ExcludePatterns = new List<string> { "debug.*" }
});

// Dump window contents
logger.DumpWindow();

// Get statistics
var stats = logger.GetStats();
Console.WriteLine($"Received: {stats.TotalReceived}, Logged: {stats.TotalLogged}");
```

### WindowSizeAtom

Dynamic SignalSink capacity and retention management via signals.

**Commands:**
- `window.size.set:N` - Set absolute capacity
- `window.size.increase:N` - Increase capacity by N
- `window.size.decrease:N` - Decrease capacity by N
- `window.time.set:VALUE` - Set retention duration (`30s`, `500ms`, `00:05:00`)
- `window.time.increase:VALUE` - Extend retention
- `window.time.decrease:VALUE` - Reduce retention

**Example:**
```csharp
await using var windowAtom = new WindowSizeAtom(sink);

sink.Raise("window.size.set:1000");
sink.Raise("window.time.set:30s");
```

### RateLimitAtom

Token bucket rate limiter for controlling throughput.

**Features:**
- Configurable rate per second
- Burst capacity
- Dynamic rate adjustment via signals
- Integration with System.Threading.RateLimiting

**Commands:**
- `rate.limit.set:N` - Set rate to N tokens/second
- `rate.limit.increase:N` - Increase rate by N
- `rate.limit.decrease:N` - Decrease rate by N
- `rate.limit.burst:N` - Set burst capacity

**Example:**
```csharp
await using var rateLimiter = new RateLimitAtom(sink, new RateLimitOptions
{
    InitialRatePerSecond = 10,
    Burst = 20
});

// Acquire lease before processing
using var lease = await rateLimiter.AcquireAsync();
if (lease.IsAcquired)
{
    // Process request
}

// Adjust rate dynamically
sink.Raise("rate.limit.set:50");
```

## Pattern Philosophy

### When to Use Each Model

| Model | Use Case | Example |
|-------|----------|---------|
| **Pure Notification** | Domain events, state changes | `file.saved`, `user.registered`, `order.placed` |
| **Context + Hint** | Performance optimization with safety | `cache.expired:key123`, `order.placed:ORD-123` |
| **Command** | Infrastructure control, admin ops | `window.size.set:500`, `log.level.set:debug` |

### Anti-Patterns (Don't Do This)

❌ **Using signal as state carrier for domain logic:**
```csharp
sink.Raise($"file.saved:{filename}:{fileSize}:{timestamp}");
```

❌ **Trusting hint without verification:**
```csharp
var orderId = signal.Signal.Split(':')[1]; // Use blindly
ProcessOrder(orderId); // Never verify with atom
```

❌ **Using command pattern for domain events:**
```csharp
sink.Raise("user.register:john@example.com"); // Wrong!
```

### Best Practices

✅ **Query atom for authoritative state:**
```csharp
sink.Raise("file.saved");
var filename = fileAtom.GetCurrentFilename(); // Query atom
```

✅ **Use hint, verify with atom:**
```csharp
var hintId = ExtractHint(signal.Signal);
UseHintForFastPath(hintId);
var actualId = atom.GetActualId(); // Verify
```

✅ **Command pattern for infrastructure only:**
```csharp
sink.Raise("window.size.set:500"); // Config only
```

## Architecture

```
SignalSink (central hub)
    ├─ WindowSizeAtom (infrastructure)
    ├─ RateLimitAtom (infrastructure)
    ├─ TestAtom (simulation)
    ├─ ConsoleSignalLoggerAtom (observability)
    └─ Your atoms (domain logic)

Signal Flow:
    User/System → SignalSink.Raise()
              → All subscribed atoms receive event
              → Atoms update their state
              → Atoms may emit new signals
              → Listeners query atoms for state
```

## Related Documentation

- [SIGNALS_PATTERN.md](../../SIGNALS_PATTERN.md) - Full pattern documentation
- [WindowSizeAtom README](../../src/mostlylucid.ephemeral.atoms.windowsize/README.md)
- [RateLimitAtom](../../src/mostlylucid.ephemeral.atoms.ratelimit/)
- [CHANGELOG.md](../../CHANGELOG.md) - Recent updates

## License

Unlicense - Public Domain

## Contributing

Issues and PRs welcome at: https://github.com/scottgal/mostlylucid.atoms
