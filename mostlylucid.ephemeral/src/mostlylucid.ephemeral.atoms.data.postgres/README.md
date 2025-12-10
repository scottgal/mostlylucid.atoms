# Mostlylucid.Ephemeral.Atoms.Data.Postgres

PostgreSQL data storage atom for signal-driven persistence. Provides key-value storage with native JSONB support for
efficient querying.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


## Installation

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.Data.Postgres
```

## Quick Start

```csharp
var signals = new SignalSink();

await using var storage = new PostgresDataStorageAtom<string, Order>(
    signals,
    databaseName: "orders",
    connectionString: "Host=localhost;Database=myapp;Username=user;Password=pass");

// Save
await storage.SaveAsync("order-123", new Order { Id = "order-123", Total = 99.99m });

// Load
var order = await storage.LoadAsync("order-123");
```

## Configuration

```csharp
var config = new PostgresDataStorageConfig
{
    DatabaseName = "orders",
    ConnectionString = "Host=localhost;Database=myapp;Username=user;Password=pass",
    Schema = "public",               // PostgreSQL schema
    TableName = "orders",            // Table name
    UseJsonb = true,                 // Use JSONB (recommended)
    MaxConcurrency = 1,              // Sequential writes
    EmitCompletionSignals = true,    // Emit saved.data.orders
    JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }
};

await using var storage = new PostgresDataStorageAtom<string, Order>(signals, config);
```

## Signal-Driven Usage

### Atom Style

```csharp
var signals = new SignalSink();
await using var storage = new PostgresDataStorageAtom<string, Order>(
    signals, "orders", connectionString);

// Fire-and-forget save
storage.EnqueueSave("order-123", order);

// React to completion
signals.SignalRaised += signal =>
{
    if (signal.Signal == "saved.data.orders")
        Console.WriteLine($"Order {signal.Key} persisted to PostgreSQL");
};
```

### Attribute Style

```csharp
[EphemeralJobs(SignalPrefix = "order")]
public class OrderWorkflow
{
    private readonly PostgresDataStorageAtom<string, Order> _storage;

    public OrderWorkflow(SignalSink signals, string connectionString)
    {
        _storage = new PostgresDataStorageAtom<string, Order>(
            signals, "orders", connectionString);
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
        Console.WriteLine($"Order {signal.Key} stored in PostgreSQL");
        return Task.CompletedTask;
    }
}
```

## Database Schema

The atom creates a table with JSONB storage:

```sql
CREATE TABLE public.orders (
    key TEXT PRIMARY KEY NOT NULL,
    value JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);
```

## JSONB Query Support

PostgreSQL JSONB enables powerful queries on stored data:

```csharp
// Find all orders with status = "pending"
var pendingOrders = await storage.QueryByJsonPathAsync("status", "pending");

// Find orders by customer ID
var customerOrders = await storage.QueryByJsonPathAsync("customerId", "cust-456");
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

// Clear all (uses TRUNCATE)
await storage.ClearAsync();
```

## Performance

- Uses `NpgsqlDataSource` for efficient connection pooling
- JSONB storage with GIN indexing potential
- Upsert via `INSERT ... ON CONFLICT` for atomic save-or-update
- Lazy initialization on first operation
- Single-writer coordination via `EphemeralWorkCoordinator`

## Connection String Examples

```csharp
// Local development
"Host=localhost;Database=myapp;Username=postgres;Password=postgres"

// With pooling
"Host=localhost;Database=myapp;Username=user;Password=pass;Pooling=true;MinPoolSize=5;MaxPoolSize=20"

// SSL connection
"Host=prod.db.example.com;Database=myapp;Username=user;Password=pass;SSL Mode=Require"
```