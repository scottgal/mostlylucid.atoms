# Mostlylucid.Ephemeral.Patterns.PersistentWindow

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.persistentwindow.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.persistentwindow)

Signal window that periodically persists to SQLite and restores on restart. Survives process restarts while maintaining
in-memory performance.

```bash
dotnet add package mostlylucid.ephemeral.patterns.persistentwindow
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.PersistentWindow;

await using var window = new PersistentSignalWindow(
    "Data Source=signals.db",
    flushInterval: TimeSpan.FromSeconds(30));

// On startup: restore previous signals
await window.LoadFromDiskAsync(maxAge: TimeSpan.FromHours(24));

// Raise signals as normal
window.Raise("order.completed", key: "order-service");
window.Raise("payment.processed", key: "payment-service");

// Query signals
var recentOrders = window.Sense("order.*");

// Signals automatically flush every 30 seconds
// Also flushes on dispose
```

---

## All Options

```csharp
new PersistentSignalWindow(
    // Required: SQLite connection string
    connectionString: "Data Source=signals.db",

    // How often to flush to disk
    // Default: 30 seconds
    flushInterval: TimeSpan.FromSeconds(30),

    // Max signals to persist per flush
    // Default: 1000
    maxSignalsPerFlush: 1000,

    // Max signals in memory window
    // Default: 10000
    maxWindowSize: 10000,

    // Max age of signals in memory
    // Default: 10 minutes
    windowMaxAge: TimeSpan.FromMinutes(10),

    // Signal sampling rate for diagnostics
    // Default: 10 (1 in 10)
    sampleRate: 10
)
```

---

## API Reference

```csharp
// Raise signals
void Raise(string signal, string? key = null);
void Raise(SignalEvent evt);

// Query signals by pattern
IReadOnlyList<SignalEvent> Sense(string? pattern = null);
IReadOnlyList<SignalEvent> Sense(Func<SignalEvent, bool> predicate);

// Force immediate flush to SQLite
Task FlushAsync(CancellationToken ct = default);

// Load signals from SQLite (call on startup)
Task LoadFromDiskAsync(TimeSpan? maxAge = null, CancellationToken ct = default);

// Get statistics
WindowStats GetStats(); // (InMemoryCount, TotalRaised, LastFlushedId)

// Access underlying sink for advanced usage
SignalSink Sink { get; }

// Dispose (flushes remaining signals)
ValueTask DisposeAsync();
```

---

## How It Works

```
                    ┌─────────────────────────────────────┐
    Raise() ───────>│  In-Memory SignalSink (fast)        │
                    │  - maxWindowSize: 10000              │
                    │  - windowMaxAge: 10 minutes          │
                    └─────────────┬───────────────────────┘
                                  │
                                  │ Every 30 seconds (flushInterval)
                                  ▼
                    ┌─────────────────────────────────────┐
                    │  SQLite (durable)                   │
                    │  - Single-writer coordination       │
                    │  - WAL mode for performance         │
                    │  - Indexed by timestamp & signal    │
                    └─────────────────────────────────────┘
                                  │
                                  │ On startup: LoadFromDiskAsync()
                                  ▼
                    ┌─────────────────────────────────────┐
                    │  Restored signals back to memory    │
                    └─────────────────────────────────────┘
```

---

## Signals Emitted

| Signal                       | Description              |
|------------------------------|--------------------------|
| `window.initialized`         | SQLite schema created    |
| `window.raise`               | Signal raised (sampled)  |
| `window.flush.start:{count}` | Starting flush           |
| `window.flush.done:{count}`  | Flush completed          |
| `window.flush.error`         | Flush failed             |
| `window.load.done:{count}`   | Signals loaded from disk |

---

## Example: Error Monitoring with Persistence

```csharp
await using var window = new PersistentSignalWindow(
    "Data Source=errors.db",
    flushInterval: TimeSpan.FromSeconds(10),
    maxWindowSize: 50000);

// On startup: restore last 24 hours of errors
await window.LoadFromDiskAsync(maxAge: TimeSpan.FromHours(24));

// In your error handler
try
{
    await ProcessRequest();
}
catch (Exception ex)
{
    window.Raise($"error:{ex.GetType().Name}", key: Environment.MachineName);
}

// Dashboard query
var last5Minutes = window.Sense(s =>
    s.Signal.StartsWith("error:") &&
    s.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-5));

Console.WriteLine($"Errors in last 5 min: {last5Minutes.Count}");
```

---

## Example: Distributed Event Tracking

```csharp
// Each service instance has its own window
await using var window = new PersistentSignalWindow(
    $"Data Source=events_{Environment.MachineName}.db",
    windowMaxAge: TimeSpan.FromHours(1));

// Restore on startup
await window.LoadFromDiskAsync(maxAge: TimeSpan.FromHours(1));

// Track events
window.Raise("user.login", key: userId);
window.Raise("order.placed", key: orderId);

// Query for patterns
var userActivity = window.Sense("user.*");
var orderEvents = window.Sense("order.*");
```

---

## Example: Graceful Shutdown

```csharp
var window = new PersistentSignalWindow("Data Source=app.db");

// Handle shutdown signal
Console.CancelKeyPress += async (s, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Flushing signals...");
    await window.FlushAsync();
    await window.DisposeAsync();
    Environment.Exit(0);
};

// Or in ASP.NET Core
public class SignalWindowService : IHostedService
{
    private readonly PersistentSignalWindow _window;

    public async Task StartAsync(CancellationToken ct)
    {
        await _window.LoadFromDiskAsync(TimeSpan.FromHours(24), ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _window.FlushAsync(ct);
        await _window.DisposeAsync();
    }
}
```

---

## Database Schema

The window creates these tables automatically:

```sql
CREATE TABLE signals (
    id INTEGER PRIMARY KEY,
    operation_id INTEGER NOT NULL,
    signal TEXT NOT NULL,
    key TEXT,
    timestamp TEXT NOT NULL,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_signals_timestamp ON signals(timestamp);
CREATE INDEX idx_signals_signal ON signals(signal);
```

---

## Related Packages

| Package                                                                                                                           | Description     |
|-----------------------------------------------------------------------------------------------------------------------------------|-----------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                     | Core library    |
| [mostlylucid.ephemeral.sqlite.singlewriter](https://www.nuget.org/packages/mostlylucid.ephemeral.sqlite.singlewriter)             | SQLite helper   |
| [mostlylucid.ephemeral.patterns.signallogwatcher](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signallogwatcher) | Signal watching |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                                   | All in one DLL  |

## License

Unlicense (public domain)
