using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral.Sqlite;

/// <summary>
/// Self-focusing LRU cache that uses an ephemeral coordinator to track access patterns.
/// Hot keys stay cached longer; cold keys are evicted faster.
/// Demonstrates: singleton coordinator for background eviction, signal-based access tracking.
/// </summary>
public sealed class EphemeralLruCache<TKey, TValue> : IAsyncDisposable where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _cache = new();
    private readonly EphemeralWorkCoordinator<EvictionWork> _evictionCoordinator;
    private readonly SignalSink _signals;
    private readonly TimeSpan _defaultTtl;
    private readonly TimeSpan _hotKeyExtension;
    private readonly int _hotAccessThreshold;
    private readonly int _maxSize;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _evictionLoop;
    private int _accessCount;
    private readonly int _sampleRate;

    public EphemeralLruCache(EphemeralLruCacheOptions? options = null)
    {
        options ??= new EphemeralLruCacheOptions();
        _defaultTtl = options.DefaultTtl;
        _hotKeyExtension = options.HotKeyExtension;
        _hotAccessThreshold = options.HotAccessThreshold;
        _maxSize = options.MaxSize;
        _sampleRate = Math.Max(1, options.SampleRate);

        _signals = new SignalSink(maxCapacity: _maxSize * 2, maxAge: TimeSpan.FromMinutes(5));

        // Singleton eviction coordinator - background cleanup
        _evictionCoordinator = new EphemeralWorkCoordinator<EvictionWork>(
            async (work, ct) =>
            {
                if (work.Type == EvictionWorkType.Evict && _cache.TryRemove(work.Key, out _))
                {
                    _signals.Raise(new SignalEvent($"cache.evict:{work.Key}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,  // Single eviction thread
                MaxTrackedOperations = 64,
                Signals = _signals
            });

        _evictionLoop = Task.Run(RunEvictionLoopAsync);
    }

    /// <summary>
    /// Gets or adds a value. Hot keys (frequently accessed) get extended TTL.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(key, out var entry))
        {
            entry.AccessCount++;
            entry.LastAccess = now;

            // Hot key? Extend expiry
            if (entry.AccessCount >= _hotAccessThreshold)
            {
                entry.Expiry = now + _hotKeyExtension;
                if (ShouldSample())
                    _signals.Raise(new SignalEvent($"cache.hot:{key}", EphemeralIdGenerator.NextId(), null, now));
            }
            else if (ShouldSample())
            {
                _signals.Raise(new SignalEvent($"cache.hit:{key}", EphemeralIdGenerator.NextId(), null, now));
            }

            return entry.Value;
        }

        // Miss - create new entry
        var value = factory(key);
        var newEntry = new CacheEntry(value, now + _defaultTtl, now);

        if (_cache.TryAdd(key, newEntry))
        {
            if (ShouldSample())
                _signals.Raise(new SignalEvent($"cache.miss:{key}", EphemeralIdGenerator.NextId(), null, now));

            // Enforce max size
            if (_cache.Count > _maxSize)
                _ = _evictionCoordinator.EnqueueAsync(new EvictionWork(key, EvictionWorkType.TriggerCleanup));
        }

        return value;
    }

    /// <summary>
    /// Async version with factory.
    /// </summary>
    public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(key, out var entry))
        {
            entry.AccessCount++;
            entry.LastAccess = now;

            if (entry.AccessCount >= _hotAccessThreshold)
            {
                entry.Expiry = now + _hotKeyExtension;
                if (ShouldSample())
                    _signals.Raise(new SignalEvent($"cache.hot:{key}", EphemeralIdGenerator.NextId(), null, now));
            }
            else if (ShouldSample())
            {
                _signals.Raise(new SignalEvent($"cache.hit:{key}", EphemeralIdGenerator.NextId(), null, now));
            }

            return entry.Value;
        }

        var value = await factory(key);
        var newEntry = new CacheEntry(value, now + _defaultTtl, now);

        if (_cache.TryAdd(key, newEntry))
        {
            if (ShouldSample())
                _signals.Raise(new SignalEvent($"cache.miss:{key}", EphemeralIdGenerator.NextId(), null, now));

            if (_cache.Count > _maxSize)
                _ = _evictionCoordinator.EnqueueAsync(new EvictionWork(key, EvictionWorkType.TriggerCleanup));
        }

        return value;
    }

    /// <summary>
    /// Manually invalidate a key.
    /// </summary>
    public void Invalidate(TKey key)
    {
        if (_cache.TryRemove(key, out _))
            _signals.Raise(new SignalEvent($"cache.invalidate:{key}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Get recent cache signals for observability.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals() => _signals.Sense();

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    public CacheStats GetStats()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = _cache.ToArray();
        var hotCount = entries.Count(e => e.Value.AccessCount >= _hotAccessThreshold);
        var expiredCount = entries.Count(e => e.Value.Expiry < now);

        return new CacheStats(_cache.Count, hotCount, expiredCount, _maxSize);
    }

    private bool ShouldSample()
    {
        var count = Interlocked.Increment(ref _accessCount);
        return count % _sampleRate == 0;
    }

    private async Task RunEvictionLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);

                var now = DateTimeOffset.UtcNow;
                var toEvict = _cache
                    .Where(kvp => kvp.Value.Expiry < now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toEvict)
                {
                    await _evictionCoordinator.EnqueueAsync(new EvictionWork(key, EvictionWorkType.Evict));
                }

                // Size-based eviction: remove coldest keys
                if (_cache.Count > _maxSize)
                {
                    var coldKeys = _cache
                        .OrderBy(kvp => kvp.Value.AccessCount)
                        .ThenBy(kvp => kvp.Value.LastAccess)
                        .Take(_cache.Count - _maxSize)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in coldKeys)
                    {
                        await _evictionCoordinator.EnqueueAsync(new EvictionWork(key, EvictionWorkType.Evict));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { /* swallow to keep loop alive */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _evictionLoop; } catch { }
        _cts.Dispose();
        await _evictionCoordinator.DisposeAsync();
    }

    private sealed class CacheEntry
    {
        public TValue Value { get; }
        public DateTimeOffset Expiry { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public int AccessCount { get; set; }

        public CacheEntry(TValue value, DateTimeOffset expiry, DateTimeOffset lastAccess)
        {
            Value = value;
            Expiry = expiry;
            LastAccess = lastAccess;
            AccessCount = 1;
        }
    }

    private readonly record struct EvictionWork(TKey Key, EvictionWorkType Type);
    private enum EvictionWorkType { Evict, TriggerCleanup }
}

/// <summary>
/// Options for EphemeralLruCache.
/// </summary>
public sealed class EphemeralLruCacheOptions
{
    /// <summary>Default TTL for new entries. Default: 5 minutes.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Extended TTL for hot keys. Default: 30 minutes.</summary>
    public TimeSpan HotKeyExtension { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Access count to be considered "hot". Default: 5.</summary>
    public int HotAccessThreshold { get; set; } = 5;

    /// <summary>Maximum cache size. Default: 1000.</summary>
    public int MaxSize { get; set; } = 1000;

    /// <summary>Signal sampling rate. 1 = all, 10 = 1 in 10. Default: 1.</summary>
    public int SampleRate { get; set; } = 1;
}

/// <summary>
/// Cache statistics snapshot.
/// </summary>
public readonly record struct CacheStats(int TotalEntries, int HotEntries, int ExpiredEntries, int MaxSize);
