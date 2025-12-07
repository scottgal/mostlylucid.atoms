# Mostlylucid.Ephemeral.Sqlite.SingleWriter

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.sqlite.singlewriter.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.sqlite.singlewriter)

SQLite single-writer helper using Ephemeral patterns for serialized writes, cached reads, and signal-based observability.

```bash
dotnet add package mostlylucid.ephemeral.sqlite.singlewriter
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Sqlite;

var writer = SqliteSingleWriter.GetOrCreate(
    "Data Source=mydb.sqlite;Mode=ReadWriteCreate;Cache=Shared");

// Serialized writes (single-writer pattern)
await writer.WriteAsync("INSERT INTO Users (Name) VALUES (@Name)", new { Name = "Alice" });

// Cached reads
var userCount = await writer.ReadAsync("users:count", async conn =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Users";
    return (int)(long)await cmd.ExecuteScalarAsync();
});

// Write and invalidate cache
await writer.WriteAndInvalidateAsync(
    "INSERT INTO Users (Name) VALUES ('Bob')",
    cacheKeysToInvalidate: new[] { "users:count" });
```

---

## All Options

### SqliteSingleWriterOptions

```csharp
var options = new SqliteSingleWriterOptions
{
    // Maximum items in cache
    // Default: 1000
    CacheSizeLimit = 1000,

    // Default cache duration (sliding expiration)
    // Default: 5 minutes
    DefaultCacheDuration = TimeSpan.FromMinutes(5),

    // Max write operations tracked in memory
    // Default: 128
    MaxTrackedWrites = 128,

    // Signal sampling rate (1 = all, 10 = 1 in 10)
    // Default: 1
    SampleRate = 1,

    // PRAGMA busy_timeout for all connections
    // Default: 10 seconds
    BusyTimeout = TimeSpan.FromSeconds(10),

    // Default command timeout
    // Default: 30 seconds
    DefaultCommandTimeoutSeconds = 30,

    // Enable WAL mode on writer connection
    // Default: true
    EnableWriteAheadLogging = true,

    // Enforce foreign keys on all connections
    // Default: true
    EnforceForeignKeys = true
};

var writer = SqliteSingleWriter.GetOrCreate(connectionString, options);
```

### EphemeralLruCacheOptions

```csharp
var cache = new EphemeralLruCache<string, User>(new EphemeralLruCacheOptions
{
    // Default TTL for new entries
    // Default: 5 minutes
    DefaultTtl = TimeSpan.FromMinutes(5),

    // Extended TTL for hot keys
    // Default: 30 minutes
    HotKeyExtension = TimeSpan.FromMinutes(30),

    // Access count to be "hot"
    // Default: 5
    HotAccessThreshold = 5,

    // Maximum cache entries
    // Default: 1000
    MaxSize = 1000,

    // Signal sampling rate
    // Default: 1
    SampleRate = 1
});
```

---

## API Reference

### SqliteSingleWriter

```csharp
// Get or create instance (keyed by connection string)
var writer = SqliteSingleWriter.GetOrCreate(connectionString, options);

// Serialized write
Task<int> WriteAsync(string sql, object? parameters = null, CancellationToken ct = default);

// Batch write (transactional)
Task<int> WriteBatchAsync(IEnumerable<(string Sql, object? Parameters)> commands,
    bool transactional = true, CancellationToken ct = default);

// Transaction with user code
Task ExecuteInTransactionAsync(
    Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work,
    CancellationToken ct = default);

Task<T> ExecuteInTransactionAsync<T>(
    Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
    CancellationToken ct = default);

// Write and invalidate cache
Task<int> WriteAndInvalidateAsync(string sql, object? parameters = null,
    IEnumerable<string>? cacheKeysToInvalidate = null, CancellationToken ct = default);

// Cached read
Task<T?> ReadAsync<T>(string cacheKey, Func<SqliteConnection, Task<T>> reader,
    MemoryCacheEntryOptions? cacheOptions = null, CancellationToken ct = default);

// Uncached query
Task<T> QueryAsync<T>(Func<SqliteConnection, Task<T>> reader, CancellationToken ct = default);

// Signal observability
IReadOnlyList<SignalEvent> GetSignals();
IReadOnlyList<SignalEvent> GetSignals(string pattern);
IReadOnlyCollection<EphemeralOperationSnapshot> GetWriteSnapshot();

// Cache management
void InvalidateCache(string cacheKey);
Task FlushWritesAsync(CancellationToken ct = default);

// External signal-driven invalidation
void EnableSignalDrivenInvalidation(SignalSink sink,
    IEnumerable<string>? patterns = null, TimeSpan? pollInterval = null);

// Dispose
ValueTask DisposeAsync();
```

### EphemeralLruCache

```csharp
// Get or add (sync)
TValue GetOrAdd(TKey key, Func<TKey, TValue> factory);

// Get or add (async)
Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory);

// Invalidate
void Invalidate(TKey key);

// Signals
IReadOnlyList<SignalEvent> GetSignals();

// Stats
CacheStats GetStats(); // (TotalEntries, HotEntries, ExpiredEntries, MaxSize)

// Dispose
ValueTask DisposeAsync();
```

---

## Signals Emitted

| Signal | Description |
|--------|-------------|
| `write.enqueue` | Write queued |
| `write.start` | Write started |
| `write.done:Nrows:Nms` | Write completed with row count and duration |
| `write.error:message` | Write failed |
| `write.batch.enqueue` | Batch write queued |
| `write.tx.enqueue` | Transaction queued |
| `write.flush.start` | Flush started |
| `write.flush.done` | Flush completed |
| `cache.hit:key` | Cache hit |
| `cache.miss:key` | Cache miss |
| `cache.set:key` | Cache entry set |
| `cache.invalidate:key` | Cache entry invalidated |
| `cache.hot:key` | Key became hot |
| `cache.evict:key` | Key evicted |
| `read.start:key` | Read started |
| `read.done:key` | Read completed |
| `query.start` | Query started |
| `connection.open.write` | Writer connection opened |
| `connection.open.read` | Reader connection opened |
| `pragma.busy_timeout` | Busy timeout pragma set |
| `pragma.foreign_keys.on` | Foreign keys enabled |
| `pragma.wal.on` | WAL mode enabled |

---

## Patterns Demonstrated

### 1. Single-Writer Coordination

```csharp
// MaxConcurrency=1 ensures serialized writes - no locks needed
_writeCoordinator = new EphemeralWorkCoordinator<WriteCommand>(
    async (cmd, ct) => await ExecuteWriteInternalAsync(cmd, ct),
    new EphemeralOptions { MaxConcurrency = 1 });
```

### 2. Sampling for Observability

```csharp
var options = new SqliteSingleWriterOptions { SampleRate = 10 };  // 1 in 10 ops
var writer = SqliteSingleWriter.GetOrCreate(connString, options);

// Observe what's happening
var writeSignals = writer.GetSignals("write.*");
var cacheSignals = writer.GetSignals("cache.*");
```

### 3. Self-Focusing LRU Cache

Hot keys automatically get extended TTL:

```csharp
var cache = new EphemeralLruCache<string, User>(new EphemeralLruCacheOptions
{
    DefaultTtl = TimeSpan.FromMinutes(5),
    HotKeyExtension = TimeSpan.FromMinutes(30),
    HotAccessThreshold = 5  // 5 hits = "hot"
});

// First 5 accesses: 5 min TTL
// After 5 accesses: 30 min TTL (hot)
var user = cache.GetOrAdd("user:123", key => LoadUser(key));
```

---

## Example: Full Usage

```csharp
var writer = SqliteSingleWriter.GetOrCreate(
    "Data Source=mydb.sqlite;Mode=ReadWriteCreate;Cache=Shared",
    new SqliteSingleWriterOptions
    {
        SampleRate = 5,
        BusyTimeout = TimeSpan.FromSeconds(10),
        EnableWriteAheadLogging = true
    });

// Serialized writes
await writer.WriteAsync("INSERT INTO Users (Name) VALUES (@Name)", new { Name = "Alice" });

// Transaction
var count = await writer.ExecuteInTransactionAsync(async (conn, tx, ct) =>
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "INSERT INTO Users (Name) VALUES ('Bob')";
    await cmd.ExecuteNonQueryAsync(ct);

    await using var countCmd = conn.CreateCommand();
    countCmd.Transaction = tx;
    countCmd.CommandText = "SELECT COUNT(*) FROM Users";
    return (int)(long)await countCmd.ExecuteScalarAsync(ct);
});

// Batch writes (transactional)
await writer.WriteBatchAsync(new[]
{
    ("INSERT INTO Users (Name) VALUES ('Charlie')", (object?)null),
    ("INSERT INTO Users (Name) VALUES ('Dana')", (object?)null)
});

// Cached read
var userCount = await writer.ReadAsync("users:count", async conn =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Users";
    return (int)(long)await cmd.ExecuteScalarAsync();
});

// Write and invalidate
await writer.WriteAndInvalidateAsync(
    "INSERT INTO Users (Name) VALUES ('Eve')",
    cacheKeysToInvalidate: new[] { "users:count" });

// Observe signals
var writeSignals = writer.GetSignals("write.*");
var cacheSignals = writer.GetSignals("cache.*");
```

---

## Example: External Signal-Driven Invalidation

Multiple processes can share cache invalidation via signals:

```csharp
var sharedSink = new SignalSink();

var writer = SqliteSingleWriter.GetOrCreate(connectionString);
writer.EnableSignalDrivenInvalidation(sharedSink);

// Any process can invalidate cache keys
sharedSink.Raise(new SignalEvent("cache.invalidate:users:count",
    EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));

// Writer automatically clears its local cache for "users:count"
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
