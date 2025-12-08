using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Self-focusing LRU cache: sliding expiration on every hit, with extended TTL for hot keys.
///     Emits signals for hit/miss/hot/evict and cleans up via a background coordinator.
/// </summary>
public sealed class EphemeralLruCache<TKey, TValue> : IAsyncDisposable where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _cache = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _defaultTtl;
    private readonly EphemeralWorkCoordinator<EvictionWork> _evictionCoordinator;
    private readonly Task _evictionLoop;
    private readonly int _hotAccessThreshold;
    private readonly TimeSpan _hotKeyExtension;
    private readonly int _maxSize;
    private readonly int _sampleRate;
    private readonly SignalSink _signals;
    private int _accessCount;

    public EphemeralLruCache(EphemeralLruCacheOptions? options = null)
    {
        options ??= new EphemeralLruCacheOptions();
        _defaultTtl = options.DefaultTtl;
        _hotKeyExtension = options.HotKeyExtension;
        _hotAccessThreshold = options.HotAccessThreshold;
        _maxSize = options.MaxSize;
        _sampleRate = Math.Max(1, options.SampleRate);

        _signals = new SignalSink(_maxSize * 2, TimeSpan.FromMinutes(5));

        // Singleton eviction coordinator - background cleanup
        _evictionCoordinator = new EphemeralWorkCoordinator<EvictionWork>(
            (work, ct) =>
            {
                if (work.Type == EvictionWorkType.Evict && _cache.TryRemove(work.Key, out _))
                    _signals.Raise(new SignalEvent($"cache.evict:{work.Key}", EphemeralIdGenerator.NextId(), null,
                        DateTimeOffset.UtcNow));

                return Task.CompletedTask;
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1, // Single eviction thread
                MaxTrackedOperations = 64,
                Signals = _signals
            });

        _evictionLoop = Task.Run(RunEvictionLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _evictionLoop;
        }
        catch
        {
        }

        _cts.Dispose();
        await _evictionCoordinator.DisposeAsync();
    }

    /// <summary>
    ///     Gets or adds a value. Sliding expiry on every hit; hot keys get extended TTL.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        return GetOrAdd(key, factory, null);
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveTtl = ttl ?? _defaultTtl;

        if (_cache.TryGetValue(key, out var entry))
        {
            entry.AccessCount++;
            entry.LastAccess = now;

            var isHot = entry.AccessCount >= _hotAccessThreshold;
            entry.Expiry = now + (isHot ? _hotKeyExtension : entry.BaseTtl); // sliding refresh; hot keys get longer TTL

            if (isHot)
            {
                if (ShouldSample())
                    _signals.Raise(new SignalEvent($"cache.hot:{key}", EphemeralIdGenerator.NextId(), null, now));
            }
            else if (ShouldSample())
            {
                _signals.Raise(new SignalEvent($"cache.hit:{key}", EphemeralIdGenerator.NextId(), null, now));
            }

            return entry.Value;
        }

        var value = factory(key);
        var newEntry = new CacheEntry(value, now + effectiveTtl, now, effectiveTtl);

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
    ///     Async version with factory.
    /// </summary>
    public Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        return GetOrAddAsync(key, factory, null);
    }

    public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory, TimeSpan? ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveTtl = ttl ?? _defaultTtl;

        if (_cache.TryGetValue(key, out var entry))
        {
            entry.AccessCount++;
            entry.LastAccess = now;

            var isHot = entry.AccessCount >= _hotAccessThreshold;
            entry.Expiry = now + (isHot ? _hotKeyExtension : entry.BaseTtl); // sliding refresh; hot keys get longer TTL

            if (isHot)
            {
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
        var newEntry = new CacheEntry(value, now + effectiveTtl, now, effectiveTtl);

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
    ///     Manually invalidate a key.
    /// </summary>
    public void Invalidate(TKey key)
    {
        if (_cache.TryRemove(key, out _))
            _signals.Raise(new SignalEvent($"cache.invalidate:{key}", EphemeralIdGenerator.NextId(), null,
                DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Get recent cache signals for observability.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return _signals.Sense();
    }

    /// <summary>
    ///     Get cache statistics.
    /// </summary>
    public CacheStats GetStats()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = _cache.ToArray();
        var hotCount = entries.Count(e => e.Value.AccessCount >= _hotAccessThreshold);
        var expiredCount = entries.Count(e => e.Value.Expiry < now);

        return new CacheStats(_cache.Count, hotCount, expiredCount, _maxSize);
    }

    /// <summary>
    ///     Try to get without invoking the factory. Applies sliding refresh on hit.
    /// </summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.Expiry < now)
            {
                _cache.TryRemove(key, out _);
                value = default;
                return false;
            }

            entry.AccessCount++;
            entry.LastAccess = now;
            var isHot = entry.AccessCount >= _hotAccessThreshold;
            entry.Expiry = now + (isHot ? _hotKeyExtension : entry.BaseTtl);

            if (isHot)
            {
                if (ShouldSample())
                    _signals.Raise(new SignalEvent($"cache.hot:{key}", EphemeralIdGenerator.NextId(), null, now));
            }
            else if (ShouldSample())
            {
                _signals.Raise(new SignalEvent($"cache.hit:{key}", EphemeralIdGenerator.NextId(), null, now));
            }

            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    private bool ShouldSample()
    {
        var count = Interlocked.Increment(ref _accessCount);
        return count % _sampleRate == 0;
    }

    private async Task RunEvictionLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);

                var now = DateTimeOffset.UtcNow;
                var toEvict = _cache
                    .Where(kvp => kvp.Value.Expiry < now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toEvict)
                    await _evictionCoordinator.EnqueueAsync(new EvictionWork(key, EvictionWorkType.Evict));

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
                        await _evictionCoordinator.EnqueueAsync(new EvictionWork(key, EvictionWorkType.Evict));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                /* swallow to keep loop alive */
            }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(TValue value, DateTimeOffset expiry, DateTimeOffset lastAccess, TimeSpan baseTtl)
        {
            Value = value;
            Expiry = expiry;
            LastAccess = lastAccess;
            AccessCount = 1;
            BaseTtl = baseTtl;
        }

        public TValue Value { get; }
        public DateTimeOffset Expiry { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public int AccessCount { get; set; }
        public TimeSpan BaseTtl { get; }
    }

    private readonly record struct EvictionWork(TKey Key, EvictionWorkType Type);

    private enum EvictionWorkType
    {
        Evict,
        TriggerCleanup
    }
}

/// <summary>
///     Options for EphemeralLruCache.
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
///     Cache statistics snapshot.
/// </summary>
public readonly record struct CacheStats(int TotalEntries, int HotEntries, int ExpiredEntries, int MaxSize);