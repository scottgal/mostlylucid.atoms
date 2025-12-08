using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral.Atoms.SlidingCache;

/// <summary>
///     Caches work results with sliding expiration - accessing a result resets its TTL.
///     Results that haven't been accessed expire and are recomputed on next request.
/// </summary>
/// <typeparam name="TKey">Cache key type</typeparam>
/// <typeparam name="TResult">Cached result type</typeparam>
public sealed class SlidingCacheAtom<TKey, TResult> : IAsyncDisposable where TKey : notnull
{
    private readonly TimeSpan _absoluteExpiration;
    private readonly ConcurrentDictionary<TKey, CacheEntry> _cache = new();
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private readonly Task _cleanupLoop;
    private readonly EphemeralWorkCoordinator<CacheRequest> _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<TKey, CancellationToken, Task<TResult>> _factory;
    private readonly int _maxSize;
    private readonly int _sampleRate;
    private readonly SignalSink _signals;
    private readonly TimeSpan _slidingExpiration;
    private int _accessCount;

    /// <summary>
    ///     Creates a new sliding cache atom.
    /// </summary>
    /// <param name="factory">Async factory to compute values for cache misses</param>
    /// <param name="slidingExpiration">Time without access before entry expires (default: 5 minutes)</param>
    /// <param name="absoluteExpiration">Maximum time entry can live regardless of access (default: 1 hour)</param>
    /// <param name="maxSize">Maximum cache entries (default: 1000)</param>
    /// <param name="maxConcurrency">Max concurrent factory calls (default: ProcessorCount)</param>
    /// <param name="sampleRate">Signal sampling rate, 1 = all, 10 = 1 in 10 (default: 1)</param>
    /// <param name="signals">Optional shared signal sink</param>
    public SlidingCacheAtom(
        Func<TKey, CancellationToken, Task<TResult>> factory,
        TimeSpan? slidingExpiration = null,
        TimeSpan? absoluteExpiration = null,
        int maxSize = 1000,
        int? maxConcurrency = null,
        int sampleRate = 1,
        SignalSink? signals = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _slidingExpiration = slidingExpiration ?? TimeSpan.FromMinutes(5);
        _absoluteExpiration = absoluteExpiration ?? TimeSpan.FromHours(1);
        _maxSize = maxSize;
        _sampleRate = Math.Max(1, sampleRate);

        _signals = signals ?? new SignalSink(maxSize * 2, TimeSpan.FromMinutes(5));

        _coordinator = new EphemeralWorkCoordinator<CacheRequest>(
            ProcessRequestAsync,
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency ?? Environment.ProcessorCount,
                MaxTrackedOperations = maxSize,
                Signals = _signals
            });

        _cleanupLoop = Task.Run(RunCleanupLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _cleanupLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        _cts.Dispose();
        _cleanupLock.Dispose();
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Gets or computes a value. Accessing a cached value resets its sliding expiration.
    /// </summary>
    public async Task<TResult> GetOrComputeAsync(TKey key, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Fast path: cache hit with valid entry
        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired(now, _slidingExpiration, _absoluteExpiration))
        {
            // Sliding expiration: reset last access time
            entry.LastAccess = now;
            entry.AccessCount++;

            if (ShouldSample())
                EmitSignal($"cache.hit:{key}");

            return entry.Value;
        }

        // Cache miss or expired - compute via coordinator
        if (ShouldSample())
            EmitSignal($"cache.miss:{key}");

        var request = new CacheRequest(key,
            new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        await _coordinator.EnqueueAsync(request, ct).ConfigureAwait(false);

        return await request.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Tries to get a cached value without triggering computation.
    ///     Still resets sliding expiration on hit.
    /// </summary>
    public bool TryGet(TKey key, out TResult? value)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired(now, _slidingExpiration, _absoluteExpiration))
        {
            entry.LastAccess = now;
            entry.AccessCount++;

            if (ShouldSample())
                EmitSignal($"cache.peek:{key}");

            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    ///     Invalidates a specific cache entry.
    /// </summary>
    public void Invalidate(TKey key)
    {
        if (_cache.TryRemove(key, out _))
            EmitSignal($"cache.invalidate:{key}");
    }

    /// <summary>
    ///     Invalidates all cache entries.
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        EmitSignal($"cache.clear:{count}");
    }

    /// <summary>
    ///     Gets cache statistics.
    /// </summary>
    public CacheStats GetStats()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = _cache.ToArray();
        var validCount = entries.Count(e => !e.Value.IsExpired(now, _slidingExpiration, _absoluteExpiration));
        var expiredCount = entries.Length - validCount;
        var hotCount = entries.Count(e => e.Value.AccessCount >= 5);

        return new CacheStats(
            entries.Length,
            validCount,
            expiredCount,
            hotCount,
            _maxSize);
    }

    /// <summary>
    ///     Gets recent cache signals.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return _signals.Sense();
    }

    /// <summary>
    ///     Gets signals matching a pattern.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(string pattern)
    {
        return _signals.Sense(s => StringPatternMatcher.Matches(s.Signal, pattern));
    }

    private async Task ProcessRequestAsync(CacheRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        try
        {
            // Double-check: another request may have populated the cache
            if (_cache.TryGetValue(request.Key, out var existing) &&
                !existing.IsExpired(now, _slidingExpiration, _absoluteExpiration))
            {
                existing.LastAccess = now;
                existing.AccessCount++;
                request.Completion.TrySetResult(existing.Value);

                if (ShouldSample())
                    EmitSignal($"cache.hit.dedup:{request.Key}");

                return;
            }

            // Compute value
            if (ShouldSample())
                EmitSignal($"cache.compute.start:{request.Key}");

            var value = await _factory(request.Key, ct).ConfigureAwait(false);

            // Store in cache
            var entry = new CacheEntry(value, now);
            _cache[request.Key] = entry;

            if (ShouldSample())
                EmitSignal($"cache.compute.done:{request.Key}");

            // Enforce max size
            if (_cache.Count > _maxSize)
                await TriggerCleanupAsync().ConfigureAwait(false);

            request.Completion.TrySetResult(value);
        }
        catch (Exception ex)
        {
            EmitSignal($"cache.error:{request.Key}:{ex.GetType().Name}");
            request.Completion.TrySetException(ex);
        }
    }

    private async Task TriggerCleanupAsync()
    {
        if (!await _cleanupLock.WaitAsync(0).ConfigureAwait(false))
            return; // Another cleanup in progress

        try
        {
            var now = DateTimeOffset.UtcNow;

            // First pass: remove expired entries
            var expired = _cache
                .Where(kvp => kvp.Value.IsExpired(now, _slidingExpiration, _absoluteExpiration))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
                if (_cache.TryRemove(key, out _))
                    EmitSignal($"cache.evict.expired:{key}");

            // Second pass: if still over size, remove coldest entries
            if (_cache.Count > _maxSize)
            {
                var toRemove = _cache
                    .OrderBy(kvp => kvp.Value.AccessCount)
                    .ThenBy(kvp => kvp.Value.LastAccess)
                    .Take(_cache.Count - _maxSize)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toRemove)
                    if (_cache.TryRemove(key, out _))
                        EmitSignal($"cache.evict.cold:{key}");
            }
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private async Task RunCleanupLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                await TriggerCleanupAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch
            {
                // Swallow to keep loop alive
            }
    }

    private bool ShouldSample()
    {
        var count = Interlocked.Increment(ref _accessCount);
        return count % _sampleRate == 0;
    }

    private void EmitSignal(string name)
    {
        _signals.Raise(new SignalEvent(name, EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
    }

    private sealed class CacheEntry
    {
        public CacheEntry(TResult value, DateTimeOffset created)
        {
            Value = value;
            Created = created;
            LastAccess = created;
            AccessCount = 1;
        }

        public TResult Value { get; }
        public DateTimeOffset Created { get; }
        public DateTimeOffset LastAccess { get; set; }
        public int AccessCount { get; set; }

        public bool IsExpired(DateTimeOffset now, TimeSpan slidingExpiration, TimeSpan absoluteExpiration)
        {
            // Absolute expiration: hard limit regardless of access
            if (now - Created > absoluteExpiration)
                return true;

            // Sliding expiration: resets on each access
            if (now - LastAccess > slidingExpiration)
                return true;

            return false;
        }
    }

    private readonly record struct CacheRequest(TKey Key, TaskCompletionSource<TResult> Completion);
}

/// <summary>
///     Cache statistics snapshot.
/// </summary>
public readonly record struct CacheStats(
    int TotalEntries,
    int ValidEntries,
    int ExpiredEntries,
    int HotEntries,
    int MaxSize);