using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class EphemeralLruCacheTests
{
    [Fact]
    public async Task SlidingRefresh_PrevailsBeforeHot()
    {
        var computeCount = 0;
        var cache = new EphemeralLruCache<string, int>(new EphemeralLruCacheOptions
        {
            DefaultTtl = TimeSpan.FromMilliseconds(80),
            HotKeyExtension = TimeSpan.FromMilliseconds(500),
            HotAccessThreshold = 3,
            SampleRate = 1000 // avoid noisy signals
        });

        try
        {
            var first = cache.GetOrAdd("k", _ =>
            {
                computeCount++;
                return 1;
            });
            Assert.Equal(1, first);

            await Task.Delay(60);
            var second = cache.GetOrAdd("k", _ =>
            {
                computeCount++;
                return 2;
            });
            Assert.Equal(1, second);

            await Task.Delay(60); // past original TTL, should survive due to sliding refresh
            var third = cache.GetOrAdd("k", _ =>
            {
                computeCount++;
                return 3;
            });
            Assert.Equal(1, third);

            Assert.Equal(1, computeCount);
        }
        finally
        {
            await cache.DisposeAsync();
        }
    }

    [Fact]
    public async Task HotKeys_GetExtendedTtl()
    {
        var computeCount = 0;
        var cache = new EphemeralLruCache<string, int>(new EphemeralLruCacheOptions
        {
            DefaultTtl = TimeSpan.FromMilliseconds(50),
            HotKeyExtension = TimeSpan.FromMilliseconds(400),
            HotAccessThreshold = 2,
            SampleRate = 1000
        });

        try
        {
            var first = cache.GetOrAdd("hot", _ =>
            {
                computeCount++;
                return 10;
            });
            Assert.Equal(10, first);

            var second = cache.GetOrAdd("hot", _ =>
            {
                computeCount++;
                return 20;
            });
            Assert.Equal(10, second);

            await Task.Delay(200); // beyond default TTL but within hot extension
            var third = cache.GetOrAdd("hot", _ =>
            {
                computeCount++;
                return 30;
            });
            Assert.Equal(10, third);

            Assert.Equal(1, computeCount);
        }
        finally
        {
            await cache.DisposeAsync();
        }
    }
}