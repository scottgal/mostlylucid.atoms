# Mostlylucid.Ephemeral.Atoms.SlidingCache

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.slidingcache.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.slidingcache)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Caches work results with sliding expiration - accessing a result resets its TTL. Results that haven't been accessed
expire and are recomputed on next request.

```bash
dotnet add package mostlylucid.ephemeral.atoms.slidingcache
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.SlidingCache;

await using var cache = new SlidingCacheAtom<string, UserProfile>(
    async (userId, ct) => await LoadUserProfileAsync(userId, ct),
    slidingExpiration: TimeSpan.FromMinutes(5));

// First call: computes and caches
var profile = await cache.GetOrComputeAsync("user-123");

// Second call within 5 minutes: returns cached, resets TTL
var cached = await cache.GetOrComputeAsync("user-123");

// After 5 minutes of no access: entry expires, recomputes
```

---

## All Options

```csharp
new SlidingCacheAtom<TKey, TResult>(
    // Required: async factory to compute values
    factory: async (key, ct) => await ComputeAsync(key, ct),

    // Time without access before entry expires
    // Default: 5 minutes
    slidingExpiration: TimeSpan.FromMinutes(5),

    // Maximum time entry can live regardless of access
    // Default: 1 hour
    absoluteExpiration: TimeSpan.FromHours(1),

    // Maximum cache entries
    // Default: 1000
    maxSize: 1000,

    // Max concurrent factory calls
    // Default: Environment.ProcessorCount
    maxConcurrency: 8,

    // Signal sampling rate (1 = all, 10 = 1 in 10)
    // Default: 1
    sampleRate: 1,

    // Shared signal sink
    // Default: null (creates internal)
    signals: sharedSink
)
```

---

## API Reference

```csharp
// Get or compute value (resets sliding expiration on hit)
Task<TResult> GetOrComputeAsync(TKey key, CancellationToken ct = default);

// Try get without triggering computation (still resets TTL on hit)
bool TryGet(TKey key, out TResult? value);

// Invalidate specific entry
void Invalidate(TKey key);

// Clear all entries
void Clear();

// Get statistics
CacheStats GetStats(); // (TotalEntries, ValidEntries, ExpiredEntries, HotEntries, MaxSize)

// Get signals
IReadOnlyList<SignalEvent> GetSignals();
IReadOnlyList<SignalEvent> GetSignals(string pattern);

// Dispose
ValueTask DisposeAsync();
```

---

## How It Works

### Sliding vs Absolute Expiration

```
Entry created at T=0, slidingExpiration=5min, absoluteExpiration=1hr

T=0:   [Created] ─────────────────────────────────────────> Absolute deadline: T=60min
       LastAccess=T=0

T=3min: [Access] ─> LastAccess=T=3min ─> Sliding deadline: T=8min

T=7min: [Access] ─> LastAccess=T=7min ─> Sliding deadline: T=12min

T=15min: [No access since T=7min] ─> Entry EXPIRED (sliding)

T=59min: [Access after recompute] ─> New entry, LastAccess=T=59min

T=61min: Entry EXPIRED (absolute deadline from T=59min creation)
```

### Eviction Strategy

When cache exceeds `maxSize`:

1. **First pass**: Remove all expired entries
2. **Second pass**: Remove coldest entries (lowest access count, then oldest access time)

---

## Signals Emitted

| Signal                      | Description                            |
|-----------------------------|----------------------------------------|
| `cache.hit:{key}`           | Cache hit, returned cached value       |
| `cache.miss:{key}`          | Cache miss, computing value            |
| `cache.peek:{key}`          | TryGet hit without computation         |
| `cache.hit.dedup:{key}`     | Hit during deduplication check         |
| `cache.compute.start:{key}` | Starting factory computation           |
| `cache.compute.done:{key}`  | Factory computation complete           |
| `cache.invalidate:{key}`    | Manual invalidation                    |
| `cache.clear:{count}`       | All entries cleared                    |
| `cache.evict.expired:{key}` | Evicted due to expiration              |
| `cache.evict.cold:{key}`    | Evicted due to size limit (cold entry) |
| `cache.error:{key}:{type}`  | Factory threw exception                |

---

## Example: API Response Caching

```csharp
await using var cache = new SlidingCacheAtom<string, ApiResponse>(
    async (endpoint, ct) =>
    {
        var response = await httpClient.GetAsync(endpoint, ct);
        return await response.Content.ReadFromJsonAsync<ApiResponse>(ct);
    },
    slidingExpiration: TimeSpan.FromMinutes(2),
    absoluteExpiration: TimeSpan.FromMinutes(30),
    maxConcurrency: 4);

// Multiple concurrent requests for same endpoint are deduplicated
var tasks = Enumerable.Range(0, 10)
    .Select(_ => cache.GetOrComputeAsync("/api/users"));

var results = await Task.WhenAll(tasks);
// Only 1 HTTP call made, all 10 tasks get same result
```

---

## Example: Database Query Caching

```csharp
await using var cache = new SlidingCacheAtom<int, Order>(
    async (orderId, ct) => await db.Orders.FindAsync(orderId, ct),
    slidingExpiration: TimeSpan.FromMinutes(10),
    maxSize: 5000,
    sampleRate: 10);  // Sample 1 in 10 for high-volume

// Hot orders stay cached, cold orders expire
var order = await cache.GetOrComputeAsync(orderId);

// Monitor cache health
var stats = cache.GetStats();
Console.WriteLine($"Hit rate estimate: {stats.HotEntries}/{stats.TotalEntries} hot");

// Check for errors
var errors = cache.GetSignals("cache.error:*");
if (errors.Any())
    logger.LogWarning("Cache errors: {Count}", errors.Count);
```

---

## Example: With Shared Signal Sink

```csharp
var sink = new SignalSink();

await using var userCache = new SlidingCacheAtom<string, User>(
    LoadUserAsync,
    signals: sink);

await using var orderCache = new SlidingCacheAtom<int, Order>(
    LoadOrderAsync,
    signals: sink);

// Monitor all cache activity
var allMisses = sink.Sense(s => s.Signal.StartsWith("cache.miss"));
var allErrors = sink.Sense(s => s.Signal.StartsWith("cache.error"));
```

---

## Related Packages

| Package                                                                                               | Description        |
|-------------------------------------------------------------------------------------------------------|--------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                         | Core library       |
| [mostlylucid.ephemeral.atoms.retry](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry) | Retry with backoff |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)       | All in one DLL     |

### Cache Strategy Comparison

Use the right cache for the job:

| Cache                                          | Expiration Model                                  | Specialization                                    | Notes                                                                 |
|------------------------------------------------|---------------------------------------------------|---------------------------------------------------|-----------------------------------------------------------------------|
| `SlidingCacheAtom`                             | Sliding on every hit + absolute max lifetime      | Dedupes concurrent computes; emits signals        | Best for async work results where every access should refresh TTL.    |
| `EphemeralLruCache` (default in sqlite helper) | Sliding on every hit; hot keys extend TTL further | Hot detection (`cache.hot`) and LRU-style cleanup | Lives in core; used by `SqliteSingleWriter` for self-focusing caches. |
| `MemoryCache` in `SqliteSingleWriter`          | Sliding only (via `MemoryCacheEntryOptions`)      | None                                              | (Replaced by `EphemeralLruCache` as the default.)                     |

> Tip: Default SQLite helper uses `EphemeralLruCache` for hot-key bias; reach for `SlidingCacheAtom` when you need async
> factories with sliding expiration and dedupe.

## License

Unlicense (public domain)