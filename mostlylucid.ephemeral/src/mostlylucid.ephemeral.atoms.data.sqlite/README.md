# Mostlylucid.Ephemeral.Atoms.Data.Sqlite

SQLite data storage atom for signal-driven persistence. Provides ACID-compliant key-value storage.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


## Installation

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.Data.Sqlite
```

## Quick Start

```csharp
var signals = new SignalSink();

// File-based SQLite
await using var storage = new SqliteDataStorageAtom<string, Order>(
    signals,
    databaseName: "orders",
    dbPath: "./orders.db");

// Or in-memory
await using var memoryStorage = new SqliteDataStorageAtom<string, Order>(
    signals,
    databaseName: "orders");

// Save
await storage.SaveAsync("order-123", new Order { Id = "order-123", Total = 99.99m });

// Load
var order = await storage.LoadAsync("order-123");
```

## Configuration

```csharp
var config = new SqliteDataStorageConfig
{
    DatabaseName = "orders",
    ConnectionString = "Data Source=./orders.db",
    TableName = "orders",            // Table name in SQLite
    UseWalMode = true,               // WAL for better concurrency
    MaxConcurrency = 1,              // Sequential writes
    EmitCompletionSignals = true,    // Emit saved.data.orders
    JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }
};

await using var storage = new SqliteDataStorageAtom<string, Order>(signals, config);
```

## Signal-Driven Usage

### Atom Style

```csharp
var signals = new SignalSink();
await using var storage = new SqliteDataStorageAtom<string, Order>(signals, "orders", "./orders.db");

// Fire-and-forget save
storage.EnqueueSave("order-123", order);

// React to completion
signals.SignalRaised += signal =>
{
    if (signal.Signal == "saved.data.orders")
        Console.WriteLine($"Order {signal.Key} persisted to SQLite");
};
```

### Attribute Style

```csharp
[EphemeralJobs(SignalPrefix = "order")]
public class OrderWorkflow
{
    private readonly SqliteDataStorageAtom<string, Order> _storage;

    public OrderWorkflow(SignalSink signals)
    {
        _storage = new SqliteDataStorageAtom<string, Order>(signals, "orders", "./orders.db");
    }

    [EphemeralJob("created")]
    public async Task HandleOrderCreated(SignalEvent signal, Order order)
    {
        await _storage.SaveAsync(order.Id, order);
        // saved.data.orders emitted automatically
    }

    [EphemeralJob("saved.data.orders")]
    public Task OnOrderPersisted(SignalEvent signal)
    {
        Console.WriteLine($"Order {signal.Key} stored in SQLite");
        return Task.CompletedTask;
    }
}
```

## Database Schema

The atom creates a simple key-value table:

```sql
CREATE TABLE orders (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL,           -- JSON-serialized value
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT DEFAULT CURRENT_TIMESTAMP
);
```

## Additional Methods

```csharp
// Count entries
long count = await storage.CountAsync();

// List all keys
var keys = await storage.ListKeysAsync();

// Check existence
bool exists = await storage.ExistsAsync("order-123");

// Delete
await storage.DeleteAsync("order-123");

// Clear all
await storage.ClearAsync();
```

## Performance

- Uses WAL journal mode for concurrent reads during writes
- Single-writer pattern via `EphemeralWorkCoordinator`
- Upsert via `INSERT ... ON CONFLICT` for atomic save-or-update
- Lazy initialization on first operation