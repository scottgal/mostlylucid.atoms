using System.Collections.Concurrent;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class SlidingCacheAtomTests
{
    private static readonly TimeSpan DefaultMaxFactoryDuration = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ColdEviction_InvokesOnEvict_WithKeyAndValue()
    {
        var evicted = new ConcurrentBag<(string key, string value)>();

        // maxSize=1; retention pins "keep" high so "evictme" is the cold casualty (deterministic).
        await using var cache = new SlidingCacheAtom<string, string>(
            (key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration,
            maxSize: 1,
            retentionScorer: (key, _) => key == "keep" ? 100.0 : 0.0,
            onEvict: (key, value, _) =>
            {
                evicted.Add((key, value));
                return ValueTask.CompletedTask;
            });

        await cache.GetOrComputeAsync("keep");
        await cache.GetOrComputeAsync("evictme"); // count 2 > maxSize 1 → cold eviction of "evictme"

        Assert.Contains(("evictme", "val:evictme"), evicted);
        Assert.False(cache.TryGet("evictme", out _), "evicted entry must be gone");
        Assert.True(cache.TryGet("keep", out _), "pinned entry must survive");
    }

    [Fact]
    public async Task Dispose_FlushesRemainingLiveEntries_ThroughOnEvict()
    {
        var flushed = new ConcurrentBag<(string key, string value)>();

        var cache = new SlidingCacheAtom<string, string>(
            (key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration,
            maxSize: 10,
            onEvict: (key, value, _) =>
            {
                flushed.Add((key, value));
                return ValueTask.CompletedTask;
            });

        await cache.GetOrComputeAsync("a");
        await cache.GetOrComputeAsync("b");

        await cache.DisposeAsync(); // graceful shutdown must not silently drop live entries

        Assert.Contains(("a", "val:a"), flushed);
        Assert.Contains(("b", "val:b"), flushed);
    }

    [Fact]
    public async Task ExpiredEviction_InvokesOnEvict_WithKeyAndValue()
    {
        // Coverage for the sibling removal site (expiry sweep) wired alongside cold eviction.
        var evicted = new ConcurrentBag<(string key, string value)>();

        await using var cache = new SlidingCacheAtom<string, string>(
            (key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration,
            slidingExpiration: TimeSpan.FromMilliseconds(50),
            absoluteExpiration: TimeSpan.FromMilliseconds(50),
            maxSize: 10,
            cleanupInterval: TimeSpan.FromMilliseconds(40),
            onEvict: (key, value, _) =>
            {
                evicted.Add((key, value));
                return ValueTask.CompletedTask;
            });

        await cache.GetOrComputeAsync("ephemeral");
        await Task.Delay(300); // let it expire and the cleanup loop sweep it

        Assert.Contains(("ephemeral", "val:ephemeral"), evicted);
    }

    [Fact]
    public async Task OnEvictThrowing_IsIsolated_DoesNotBubbleAndOtherEntriesStillFlush()
    {
        var flushed = new ConcurrentBag<string>();

        var cache = new SlidingCacheAtom<string, string>(
            (key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration,
            maxSize: 10,
            onEvict: (key, _, _) =>
            {
                if (key == "bad") throw new InvalidOperationException("persist failed");
                flushed.Add(key);
                return ValueTask.CompletedTask;
            });

        await cache.GetOrComputeAsync("bad");
        await cache.GetOrComputeAsync("good");

        // A throwing callback must not bubble out of disposal, and must not stop the others.
        await cache.DisposeAsync();

        Assert.Contains("good", flushed);
    }

    [Fact]
    public async Task SlidingExpiration_IsResetOnAccess()
    {
        var computeCount = 0;

        await using var cache = new SlidingCacheAtom<string, int>(
            (_, _) =>
            {
                computeCount++;
                return Task.FromResult(42);
            },
            maxFactoryDuration: DefaultMaxFactoryDuration,
            slidingExpiration: TimeSpan.FromMilliseconds(300),
            absoluteExpiration: TimeSpan.FromSeconds(5));

        var first = await cache.GetOrComputeAsync("key");
        Assert.Equal(42, first);

        await Task.Delay(100); // below sliding window
        var second = await cache.GetOrComputeAsync("key");
        Assert.Equal(42, second);

        await Task.Delay(100); // still below refreshed sliding window
        var third = await cache.GetOrComputeAsync("key");
        Assert.Equal(42, third);

        Assert.Equal(1, computeCount);
    }

    [Fact]
    public async Task AbsoluteExpiration_IsEnforced()
    {
        var computeCount = 0;

        await using var cache = new SlidingCacheAtom<string, int>(
            (_, _) =>
            {
                computeCount++;
                return Task.FromResult(computeCount);
            },
            maxFactoryDuration: DefaultMaxFactoryDuration,
            slidingExpiration: TimeSpan.FromSeconds(5),
            absoluteExpiration: TimeSpan.FromMilliseconds(150));

        var first = await cache.GetOrComputeAsync("key");
        Assert.Equal(1, first);

        await Task.Delay(220); // beyond absolute expiration
        var second = await cache.GetOrComputeAsync("key");

        Assert.Equal(2, computeCount); // recomputed
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task ExpiredEntries_AreRemovedWhenOverCapacity()
    {
        var computeCount = 0;

        await using var cache = new SlidingCacheAtom<string, int>(
            (_, _) =>
            {
                computeCount++;
                return Task.FromResult(computeCount);
            },
            maxFactoryDuration: DefaultMaxFactoryDuration,
            slidingExpiration: TimeSpan.FromMilliseconds(60),
            absoluteExpiration: TimeSpan.FromMilliseconds(60),
            maxSize: 1);

        var first = await cache.GetOrComputeAsync("old");
        Assert.Equal(1, first);

        await Task.Delay(90); // allow "old" to expire
        var second = await cache.GetOrComputeAsync("new");
        Assert.Equal(2, second);

        // Adding "new" triggered cleanup; "old" should be gone
        Assert.False(cache.TryGet("old", out _));

        var stats = cache.GetStats();
        Assert.Equal(1, stats.TotalEntries);
        Assert.Equal(1, stats.ValidEntries);
        Assert.Equal(0, stats.ExpiredEntries);
    }

    [Fact]
    public async Task Eviction_WithRetentionScorer_KeepsHighRiskOverHighFrequency()
    {
        // Arrange: maxSize=2, one high-risk entry (1 access), one low-risk (10 accesses)
        await using var cache = new SlidingCacheAtom<string, (double risk, int dummy)>(
            (key, _) => Task.FromResult(key == "high-risk" ? (0.9, 0) : (0.0, 0)),
            maxFactoryDuration: DefaultMaxFactoryDuration,
            maxSize: 2,
            cleanupInterval: TimeSpan.FromMilliseconds(50),
            retentionScorer: (_, v) => v.risk);

        await cache.GetOrComputeAsync("high-risk");
        for (var i = 0; i < 10; i++)
            await cache.GetOrComputeAsync("low-risk");  // boost AccessCount

        // Act: add third entry to trigger eviction
        await cache.GetOrComputeAsync("new-entry");
        await Task.Delay(200);  // let cleanup run at least once

        // Assert: high-risk must still be in cache
        Assert.True(cache.TryGet("high-risk", out _), "High-risk entry must survive over high-frequency low-risk");
    }

    [Fact]
    public async Task CleanupInterval_Tunable_ExpiredEntriesEvictedAtConfiguredRate()
    {
        await using var cache = new SlidingCacheAtom<string, int>(
            (_, _) => Task.FromResult(1),
            maxFactoryDuration: DefaultMaxFactoryDuration,
            slidingExpiration: TimeSpan.FromMilliseconds(50),
            maxSize: 10,
            cleanupInterval: TimeSpan.FromMilliseconds(60));

        await cache.GetOrComputeAsync("k1");
        await Task.Delay(300);  // 3+ cleanup sweeps at 60ms interval

        // k1 should be expired and swept
        Assert.False(cache.TryGet("k1", out _), "Expired entry should be evicted by cleanup loop");
    }

    [Fact]
    public async Task TryGetEntryStats_ReportsAccessCountAndLastAccess_ForALiveEntry()
    {
        // Consumers that want to rank/prioritize by "how hot is this entry" (e.g. a
        // materializer deciding what to warm first) must read the SAME AccessCount/LastAccess
        // this atom already tracks internally, rather than maintaining a second counter that
        // can drift from the real access pattern. This is the read-only surface for that.
        await using var cache = new SlidingCacheAtom<string, string>((key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration);

        await cache.GetOrComputeAsync("a"); // AccessCount 1 (creation)
        await cache.GetOrComputeAsync("a"); // AccessCount 2 (hit)
        await cache.GetOrComputeAsync("a"); // AccessCount 3 (hit)

        Assert.True(cache.TryGetEntryStats("a", out var stats));
        Assert.Equal(3, stats.AccessCount);
        Assert.True(stats.LastAccess <= DateTimeOffset.UtcNow);
        Assert.True(stats.LastAccess >= stats.Created);
    }

    [Fact]
    public async Task TryGetEntryStats_ReadingDoesNotItselfCountAsAnAccess()
    {
        // A read-only introspection accessor must not perturb the very stats it reports --
        // otherwise ranking consumers would inflate AccessCount just by asking.
        await using var cache = new SlidingCacheAtom<string, string>((key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration);
        await cache.GetOrComputeAsync("a");

        cache.TryGetEntryStats("a", out var first);
        cache.TryGetEntryStats("a", out var second);

        Assert.Equal(first.AccessCount, second.AccessCount);
    }

    [Fact]
    public async Task TryGetEntryStats_ReturnsFalse_ForAnUnknownKey()
    {
        await using var cache = new SlidingCacheAtom<string, string>((key, _) => Task.FromResult($"val:{key}"),
            maxFactoryDuration: DefaultMaxFactoryDuration);

        Assert.False(cache.TryGetEntryStats("missing", out var stats));
        Assert.Equal(default, stats);
    }
}
