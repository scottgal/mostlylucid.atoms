# Mostlylucid.Ephemeral.Sqlite.SingleWriter

**Complete SQLite helper + demo of Ephemeral patterns**:
- Single-writer via `EphemeralWorkCoordinator` (one writer lane, no global locks)
- Signal-based sampling for observability (writes, reads, cache, pragmas)
- Cached reads with signal-driven invalidation (local + external signals)
- Connection-string keyed instances + WAL/busy timeout pragmas
- Transaction helpers and batch writes for real workloads

## Installation

```bash
dotnet add package mostlylucid.ephemeral.sqlite.singlewriter
```

## Patterns Demonstrated

### 1. Single-Writer Coordination

```csharp
// MaxConcurrency=1 ensures serialized writes
_writeCoordinator = new EphemeralWorkCoordinator<WriteCommand>(
    async (cmd, ct) => await ExecuteWriteInternalAsync(cmd, ct),
    new EphemeralOptions { MaxConcurrency = 1 });
```

### 2. Sampling for Observability

```csharp
var options = new SqliteSingleWriterOptions { SampleRate = 10 };  // 1 in 10 ops
var writer = SqliteSingleWriter.GetOrCreate(connString, options);

// Later: observe what's happening
var writeSignals = writer.GetSignals("write.*");
var cacheSignals = writer.GetSignals("cache.*");
```

### 3. Self-Focusing LRU Cache

```csharp
var cache = new EphemeralLruCache<string, User>(new EphemeralLruCacheOptions
{
    DefaultTtl = TimeSpan.FromMinutes(5),
    HotKeyExtension = TimeSpan.FromMinutes(30),
    HotAccessThreshold = 5,  // 5 hits = "hot"
    MaxSize = 1000,
    SampleRate = 10
});

// Hot keys automatically extend TTL
var user = cache.GetOrAdd("user:123", key => LoadUser(key));
var stats = cache.GetStats();  // TotalEntries, HotEntries, ExpiredEntries
```

## Usage Example

```csharp
var writer = SqliteSingleWriter.GetOrCreate(
    "Data Source=mydb.sqlite;Mode=ReadWriteCreate;Cache=Shared",
    new SqliteSingleWriterOptions
    {
        SampleRate = 5,
        BusyTimeout = TimeSpan.FromSeconds(10),   // PRAGMA busy_timeout
        EnableWriteAheadLogging = true            // PRAGMA journal_mode=WAL
    });

// Serialized writes
await writer.WriteAsync("INSERT INTO Users (Name) VALUES (@Name)", new { Name = "Alice" });

// Transactional work on the single writer connection
var inserted = await writer.ExecuteInTransactionAsync(async (conn, tx, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "INSERT INTO Users (Name) VALUES ('Bob')";
    await cmd.ExecuteNonQueryAsync(ct);

    await using var countCmd = conn.CreateCommand();
    countCmd.Transaction = tx;
    countCmd.CommandText = "SELECT COUNT(*) FROM Users";
    var count = (long)await countCmd.ExecuteScalarAsync(ct);
    return (int)count;
});

// Batch writes (runs in a transaction by default)
await writer.WriteBatchAsync(new[]
{
    ("INSERT INTO Users (Name) VALUES ('Charlie')", (object?)null),
    ("INSERT INTO Users (Name) VALUES ('Dana')", (object?)null)
});

// Cached reads with invalidation
var userCount = await writer.ReadAsync("users:count", async conn =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Users";
    return (int)(long)await cmd.ExecuteScalarAsync();
});

await writer.WriteAndInvalidateAsync(
    "INSERT INTO Users (Name) VALUES ('Eve')",
    cacheKeysToInvalidate: new[] { "users:count" });

// Observe what's happening
var writeSignals = writer.GetSignals("write.*");
var cacheSignals = writer.GetSignals("cache.*");
```

## Why Ephemeral here?

- **Single-writer coordination**: a long-lived coordinator serializes writes per connection string—safer than sprinkling locks, and you can observe/inspect operations live.
- **Signals everywhere**: every important branch (write start/done/error, batch item start/done, tx begin/commit/rollback, cache hit/miss/set/invalidate, WAL/foreign key pragmas, flush) emits signals so you can trace behavior or hook other coordinators.
- **Cache invalidation via signals**: local invalidation on writes, plus an opt-in external sink (`EnableSignalDrivenInvalidation`) so multiple processes/services can invalidate shared cache keys with `cache.invalidate:*` signals.
- **Composable demo**: shows how Ephemeral primitives (coordinators + signal sink) let you build a tiny CQRS-ish SQLite helper that’s observable, bounded, and safe to share by key.

## License

MIT
