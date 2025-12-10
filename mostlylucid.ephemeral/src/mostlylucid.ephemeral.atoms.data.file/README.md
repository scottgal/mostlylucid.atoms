# Mostlylucid.Ephemeral.Atoms.Data.File

File-based JSON data storage atom for signal-driven persistence. Stores each key-value pair as a separate JSON file.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


## Installation

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.Data.File
```

## Quick Start

```csharp
var signals = new SignalSink();

// Simple setup
await using var storage = new FileDataStorageAtom<string, Order>(
    signals,
    databaseName: "orders",
    basePath: "./data");

// Save
await storage.SaveAsync("order-123", new Order { Id = "order-123", Total = 99.99m });

// Load
var order = await storage.LoadAsync("order-123");
```

## Configuration

```csharp
var config = new FileDataStorageConfig
{
    DatabaseName = "orders",
    BasePath = "./data",              // Storage directory
    FileExtension = ".json",          // File extension
    MaxConcurrency = 1,               // Sequential writes
    EmitCompletionSignals = true,     // Emit saved.data.orders
    JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }
};

await using var storage = new FileDataStorageAtom<string, Order>(signals, config);
```

## Signal-Driven Usage

### Atom Style

```csharp
var signals = new SignalSink();
await using var storage = new FileDataStorageAtom<string, Order>(signals, "orders");

// Fire-and-forget save
storage.EnqueueSave("order-123", order);

// Listen for completion
signals.SignalRaised += signal =>
{
    if (signal.Signal.StartsWith("saved.data.orders"))
        Console.WriteLine($"Order {signal.Key} saved!");
};
```

### Attribute Style

```csharp
[EphemeralJobs(SignalPrefix = "order")]
public class OrderHandler
{
    private readonly FileDataStorageAtom<string, Order> _storage;
    private readonly SignalSink _signals;

    public OrderHandler(SignalSink signals)
    {
        _signals = signals;
        _storage = new FileDataStorageAtom<string, Order>(signals, "orders");
    }

    [EphemeralJob("created", EmitOnComplete = new[] { "order.persisted" })]
    public async Task HandleOrderCreated(SignalEvent signal, Order order)
    {
        await _storage.SaveAsync(order.Id, order);
    }

    [EphemeralJob("saved.data.orders")]  // Listen to storage completion
    public Task OnOrderSaved(SignalEvent signal)
    {
        Console.WriteLine($"Order {signal.Key} written to disk");
        return Task.CompletedTask;
    }
}
```

## File Structure

Files are stored as:

```
./data/orders/
├── order-123.json
├── order-456.json
└── order-789.json
```

Each file contains the JSON-serialized value:

```json
{
  "id": "order-123",
  "total": 99.99,
  "items": [...]
}
```

## Additional Methods

```csharp
// Check existence
bool exists = await storage.ExistsAsync("order-123");

// Delete
await storage.DeleteAsync("order-123");

// List all keys
foreach (var key in storage.ListKeys())
    Console.WriteLine(key);

// Clear all data
storage.Clear();

// Get storage path
Console.WriteLine(storage.StoragePath); // ./data/orders
```

## Thread Safety

All operations are coordinated through an internal `EphemeralWorkCoordinator`, ensuring:

- Sequential writes by default (configurable via `MaxConcurrency`)
- Atomic file writes (write to temp, then move)
- Signal emission after successful operations