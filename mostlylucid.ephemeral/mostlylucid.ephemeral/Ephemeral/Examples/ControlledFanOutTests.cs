using System.Collections.Concurrent;
using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class ControlledFanOutTests
{
    [Fact]
    public async Task Respects_Global_And_PerKey_Limits()
    {
        var concurrent = 0;
        var maxObserved = 0;
        var perKeyObserved = new ConcurrentDictionary<string, int>();
        var perKeyMax = new ConcurrentDictionary<string, int>();

        await using var fanout = new ControlledFanOut<string, (string Key, int Value)>(
            item => item.Key,
            async (item, ct) =>
            {
                var globalNow = Interlocked.Increment(ref concurrent);
                maxObserved = Math.Max(maxObserved, globalNow);
                var keyCount = perKeyObserved.AddOrUpdate(item.Key, 1, (_, v) => v + 1);
                perKeyMax[item.Key] = Math.Max(perKeyMax.TryGetValue(item.Key, out var m) ? m : 0, keyCount);
                await Task.Delay(10, ct);
                perKeyObserved[item.Key] = keyCount - 1;
                Interlocked.Decrement(ref concurrent);
            },
            maxGlobalConcurrency: 2,
            perKeyConcurrency: 1);

        var items = new[]
        {
            ("a", 1), ("a", 2), ("b", 1), ("b", 2), ("c", 1)
        };

        foreach (var i in items) await fanout.EnqueueAsync(i);
        await fanout.DrainAsync();

        Assert.Equal(2, maxObserved); // global cap
        Assert.All(perKeyMax.Values, v => Assert.Equal(1, v)); // per-key cap
    }
}
