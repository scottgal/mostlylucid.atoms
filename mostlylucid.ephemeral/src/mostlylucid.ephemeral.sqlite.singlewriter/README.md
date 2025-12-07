# Mostlylucid.Ephemeral.Sqlite.SingleWriter

**Demo package** showing ephemeral patterns applied to SQLite access:
- Single-writer via `EphemeralWorkCoordinator` (MaxConcurrency=1)
- Signal-based sampling for observability
- Self-focusing LRU cache with ephemeral eviction coordinator
- Connection-string keyed instances

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
var writer = SqliteSingleWriter.GetOrCreate("Data Source=mydb.sqlite",
    new SqliteSingleWriterOptions { SampleRate = 10 });

// Writes are serialized, sampled for signals
await writer.WriteAsync("INSERT INTO Users (Name) VALUES (@Name)",
    new { Name = "Alice" });

// Reads are cached, concurrent, signal-tracked
var user = await writer.ReadAsync("user:1", async conn =>
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM Users WHERE Id = 1";
    // ... read logic
});

// Observe what's happening
foreach (var signal in writer.GetSignals())
{
    Console.WriteLine($"{signal.Signal} at {signal.Timestamp}");
}
```

## License

MIT
