# WindowSizeAtom - Dynamic Signal Window Management

**Dynamic window sizing atom that adjusts SignalSink capacity and retention at runtime via signals.**

## Overview

WindowSizeAtom listens for command signals and dynamically tunes `SignalSink` parameters without requiring service restarts. This enables adaptive memory management, debug modes, circuit breaker integration, and runtime configuration tuning.

## ⚠️ **Important: This Atom is an Exception to the Ephemeral Signals Pattern**

**Ephemeral Signals Philosophy**: Signals are meant to be "hey, look at me!" notifications, not state carriers.
The pattern is: **Signal provides context, atom holds state**.

**Example of the pattern:**
```csharp
// ✅ CORRECT: Signal is just a notification
sink.Raise("file.saved");
// Listener queries the atom for actual state:
var filename = fileAtom.GetCurrentFilename();

// ❌ ANTI-PATTERN: Don't do this
sink.Raise($"file.saved:{filename}"); // State in signal!
```

**WindowSizeAtom is a deliberate exception** because:
1. It's a **command pattern**, not event notification
2. The "state" (capacity/retention) is transient configuration, not business data
3. The commands are **imperative actions** ("set to 500") not events ("something happened")
4. It operates on the SignalSink itself, which is infrastructure, not domain logic

**When to use command signals (rare):**
- Infrastructure configuration (like WindowSizeAtom)
- System control commands (shutdown, pause, resume)
- Admin/debug operations (enable logging, change verbosity)

**When to use notification signals (common):**
- Domain events: "order.placed", "user.registered", "payment.completed"
- State changes: "circuit.open", "cache.expired", "connection.lost"
- Metrics: "error.occurred", "threshold.exceeded", "quota.warning"

For these, **listeners query the atom** for actual state rather than embedding it in the signal.

## Installation

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.WindowSize
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.WindowSize;

var sink = new SignalSink(maxCapacity: 100);
await using var atom = new WindowSizeAtom(sink);

// Dynamically adjust capacity
sink.Raise("window.size.set:500");

// Adjust retention time
sink.Raise("window.time.set:30s");
```

## Signal Commands

### Capacity Commands

| Signal | Description | Example |
|--------|-------------|---------|
| `window.size.set:N` | Set absolute capacity | `window.size.set:500` |
| `window.size.increase:N` | Increase capacity by N | `window.size.increase:100` |
| `window.size.decrease:N` | Decrease capacity by N | `window.size.decrease:50` |

### Retention Commands

| Signal | Description | Example |
|--------|-------------|---------|
| `window.time.set:VALUE` | Set retention duration | `window.time.set:30s` |
| `window.time.increase:VALUE` | Extend retention | `window.time.increase:10s` |
| `window.time.decrease:VALUE` | Reduce retention | `window.time.decrease:5s` |

### Time Format Support

| Format | Example | Result |
|--------|---------|--------|
| Seconds | `30s` | 30 seconds |
| Milliseconds | `500ms` | 500 milliseconds |
| TimeSpan | `00:05:00` | 5 minutes |
| TimeSpan | `01:30:00` | 1 hour 30 minutes |

**Note:** Millisecond format (`ms`) is checked before seconds (`s`) to prevent parsing "500ms" as "500m" + "s".

## Use Cases

### 1. Adaptive Memory Management

```csharp
var sink = new SignalSink(maxCapacity: 100);
await using var atom = new WindowSizeAtom(sink);

// Monitor system load
if (requestRate > highThreshold)
{
    sink.Raise("window.size.set:1000"); // Increase during high traffic
}
else if (requestRate < lowThreshold)
{
    sink.Raise("window.size.set:100"); // Decrease during low traffic
}
```

### 2. Debug Mode Toggle

```csharp
var sink = new SignalSink(maxCapacity: 100, maxAge: TimeSpan.FromMinutes(1));
await using var atom = new WindowSizeAtom(sink);

// Enable debug mode - capture more history
if (debugModeEnabled)
{
    sink.Raise("window.size.set:5000");
    sink.Raise("window.time.set:00:30:00"); // 30 minutes
}

// Restore normal mode
sink.Raise("window.size.set:100");
sink.Raise("window.time.set:00:01:00"); // 1 minute
```

### 3. Circuit Breaker Integration

```csharp
var sink = new SignalSink();
await using var atom = new WindowSizeAtom(sink);

// React to circuit breaker signals
sink.SignalRaised += signal =>
{
    if (signal.Signal == "circuit.open")
    {
        // Reduce window size when circuit opens
        sink.Raise("window.size.set:50");
        sink.Raise("window.time.set:10s");
    }
    else if (signal.Signal == "circuit.closed")
    {
        // Restore normal size when circuit closes
        sink.Raise("window.size.set:500");
        sink.Raise("window.time.set:00:05:00");
    }
};
```

### 4. Metrics-Driven Tuning

```csharp
var sink = new SignalSink();
await using var atom = new WindowSizeAtom(sink);

// Adjust based on error rate
var errorRate = CalculateErrorRate(sink);
if (errorRate > 0.1) // >10% errors
{
    // Keep more history to analyze errors
    sink.Raise("window.size.increase:500");
    sink.Raise("window.time.increase:30s");
}
```

## Configuration

### Default Options

```csharp
public class WindowSizeAtomOptions
{
    public string CapacitySetCommand { get; init; } = "window.size.set";
    public string CapacityIncreaseCommand { get; init; } = "window.size.increase";
    public string CapacityDecreaseCommand { get; init; } = "window.size.decrease";

    public string TimeSetCommand { get; init; } = "window.time.set";
    public string TimeIncreaseCommand { get; init; } = "window.time.increase";
    public string TimeDecreaseCommand { get; init; } = "window.time.decrease";

    public int MinCapacity { get; init; } = 16;
    public int MaxCapacity { get; init; } = 50_000;
    public TimeSpan MinRetention { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetention { get; init; } = TimeSpan.FromHours(1);
}
```

### Custom Configuration

```csharp
var options = new WindowSizeAtomOptions
{
    // Custom command names
    CapacitySetCommand = "capacity.set",
    TimeSetCommand = "retention.set",

    // Custom limits
    MinCapacity = 50,
    MaxCapacity = 10_000,
    MinRetention = TimeSpan.FromSeconds(1),
    MaxRetention = TimeSpan.FromHours(24)
};

await using var atom = new WindowSizeAtom(sink, options);
sink.Raise("capacity.set:500"); // Uses custom command
```

## Nested Signal Names

All commands flow through `SignalCommandMatch`, so nested signal names work seamlessly:

```csharp
// All these work identically
sink.Raise("window.size.set:100");
sink.Raise("app.window.size.set:100");
sink.Raise("production.app.window.size.set:100");
```

## Safety & Performance

### Thread Safety
- ✅ All updates are thread-safe
- ✅ Concurrent signals handled safely
- ⚠️ Rapid updates may cause lock contention on SignalSink

### Value Clamping
All values are automatically clamped to configured min/max:
```csharp
// Request 1 million capacity
sink.Raise("window.size.set:1000000");
// Actual: Clamped to MaxCapacity (50,000 by default)

// Request 1ms retention
sink.Raise("window.time.set:1ms");
// Actual: Clamped to MinRetention (5s by default)
```

### Performance
- Signal processing: **< 10 microseconds** (synchronous, fast)
- Memory impact: **Negligible** (event handler only)
- Lock contention: **Low** (only during SignalSink updates)

## Advanced Patterns

### Cascading Updates

```csharp
// Automatically reduce retention when capacity increases
sink.SignalRaised += signal =>
{
    if (SignalCommandMatch.TryParse(signal.Signal, "window.size.set", out var match))
    {
        if (int.TryParse(match.Payload, out var newCapacity) && newCapacity > 1000)
        {
            // Large capacity = shorter retention
            sink.Raise("window.time.set:30s");
        }
    }
};
```

### External Control API

```csharp
[HttpPost("api/signals/window/capacity/{value}")]
public IActionResult SetCapacity(int value)
{
    _signalSink.Raise($"window.size.set:{value}");
    return Ok(new { capacity = _signalSink.MaxCapacity });
}

[HttpPost("api/signals/window/retention/{seconds}")]
public IActionResult SetRetention(int seconds)
{
    _signalSink.Raise($"window.time.set:{seconds}s");
    return Ok(new { retention = _signalSink.MaxAge });
}
```

## Troubleshooting

### Values Not Updating

**Problem:** Signals raised but capacity/retention unchanged

**Solutions:**
1. Check signal format matches command exactly
2. Verify atom is not disposed
3. Check values aren't being clamped to limits
4. Add logging to verify signal processing

```csharp
// Debug version with logging
var sink = new SignalSink();
var atom = new WindowSizeAtom(sink);

sink.SignalRaised += s => Console.WriteLine($"Signal: {s.Signal}");
sink.Raise("window.size.set:500");
Console.WriteLine($"Capacity now: {sink.MaxCapacity}");
```

### Unexpected Clamping

**Problem:** Values being clamped to unexpected limits

**Solution:** Check your WindowSizeAtomOptions:
```csharp
var options = new WindowSizeAtomOptions
{
    MinCapacity = 10,     // Values below this will be clamped up
    MaxCapacity = 100,    // Values above this will be clamped down
    MinRetention = TimeSpan.FromSeconds(5),  // Minimum retention
    MaxRetention = TimeSpan.FromHours(1),    // Maximum retention
};
```

## Security & Performance Improvements (v1.0.1)

Recent updates include:

1. **Bounds checking in SignalCommandMatch** - Prevents potential `ArgumentOutOfRangeException` when parsing malformed signals
2. **Millisecond parsing fix** - Correctly parses "ms" suffix before "s" to avoid misinterpretation
3. **Comprehensive test coverage** - 24 new tests covering edge cases, concurrency, and error handling

## Related Components

- **SignalSink**: The signal window being controlled
- **SignalCommandMatch**: Parser for extracting command payloads (with improved safety)
- **Circuit Breaker Atoms**: Can emit signals to trigger window adjustments
- **Metrics Atoms**: Can monitor signal rates and adjust accordingly

## License

Unlicense - Public Domain

## Contributing

Issues and PRs welcome at: https://github.com/scottgal/mostlylucid.atoms
