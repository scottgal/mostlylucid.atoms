# Mostlylucid.Ephemeral.Atoms.Data

Core data storage abstractions for signal-driven persistence. This package provides the base interfaces and configuration used by storage-specific implementations.

## Installation

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.Data
```

For actual storage, install one of:
- `Mostlylucid.Ephemeral.Atoms.Data.File` - JSON file storage
- `Mostlylucid.Ephemeral.Atoms.Data.Sqlite` - SQLite database
- `Mostlylucid.Ephemeral.Atoms.Data.Postgres` - PostgreSQL database

## Configuration

All storage atoms share common configuration:

```csharp
var config = new DataStorageConfig
{
    DatabaseName = "orders",           // Used in signal patterns
    SignalPrefix = "save.data",        // save.data.orders
    LoadSignalPrefix = "load.data",    // load.data.orders
    DeleteSignalPrefix = "delete.data", // delete.data.orders
    MaxConcurrency = 1,                // Sequential writes (recommended)
    EmitCompletionSignals = true       // Emit saved.data.orders on success
};
```

## Signal Patterns

| Signal | Description |
|--------|-------------|
| `save.data.{dbname}` | Trigger a save operation |
| `load.data.{dbname}` | Trigger a load operation |
| `delete.data.{dbname}` | Trigger a delete operation |
| `saved.data.{dbname}` | Emitted after successful save |
| `deleted.data.{dbname}` | Emitted after successful delete |
| `error.data.{dbname}:{ExceptionType}` | Emitted on error |

## Usage

### Direct API (Atom Style)

```csharp
var signals = new SignalSink();
var config = new DataStorageConfig { DatabaseName = "orders" };

await using var storage = new FileDataStorageAtom<string, Order>(signals, config, "./data");

// Save directly
await storage.SaveAsync("order-123", new Order { Id = "order-123", Total = 99.99m });

// Load
var order = await storage.LoadAsync("order-123");

// Fire-and-forget via signal
storage.EnqueueSave("order-456", new Order { Id = "order-456", Total = 50.00m });
```

### Signal-Driven (Attribute Style)

```csharp
[EphemeralJobs]
public class OrderService
{
    private readonly IDataStorageAtom<string, Order> _storage;

    public OrderService(IDataStorageAtom<string, Order> storage)
    {
        _storage = storage;
    }

    [EphemeralJob("order.created")]
    public async Task OnOrderCreated(SignalEvent signal, Order order)
    {
        // Storage listens for save.data.orders automatically
        await _storage.SaveAsync(order.Id, order);
    }

    [EphemeralJob("saved.data.orders")]
    public Task OnOrderSaved(SignalEvent signal)
    {
        Console.WriteLine($"Order {signal.Key} saved successfully");
        return Task.CompletedTask;
    }
}
```

## Interfaces

### IDataStorageAtom<TKey, TValue>

```csharp
public interface IDataStorageAtom<TKey, TValue> : IAsyncDisposable
{
    DataStorageConfig Config { get; }
    Task SaveAsync(TKey key, TValue value, CancellationToken ct = default);
    Task<TValue?> LoadAsync(TKey key, CancellationToken ct = default);
    Task DeleteAsync(TKey key, CancellationToken ct = default);
    Task<bool> ExistsAsync(TKey key, CancellationToken ct = default);
}
```
