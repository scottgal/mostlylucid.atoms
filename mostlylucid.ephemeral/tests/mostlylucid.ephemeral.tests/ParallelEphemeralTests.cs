using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class ParallelEphemeralTests
{
    [Fact]
    public async Task EphemeralForEachAsync_ProcessesAllItems()
    {
        var items = Enumerable.Range(1, 10).ToList();
        var processed = new List<int>();
        var lockObj = new object();

        await items.EphemeralForEachAsync(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                lock (lockObj)
                {
                    processed.Add(item);
                }
            },
            new EphemeralOptions { MaxConcurrency = 4 });

        Assert.Equal(10, processed.Count);
        Assert.Equal(items.OrderBy(x => x), processed.OrderBy(x => x));
    }

    [Fact]
    public async Task EphemeralForEachAsync_RespectsMaxConcurrency()
    {
        var items = Enumerable.Range(1, 20).ToList();
        var running = 0;
        var maxRunning = 0;
        var lockObj = new object();

        await items.EphemeralForEachAsync(
            async (item, ct) =>
            {
                lock (lockObj)
                {
                    running++;
                    if (running > maxRunning) maxRunning = running;
                }

                await Task.Delay(50, ct);
                lock (lockObj)
                {
                    running--;
                }
            },
            new EphemeralOptions { MaxConcurrency = 3 });

        Assert.True(maxRunning <= 3, $"Max running was {maxRunning}, expected <= 3");
    }

    [Fact]
    public async Task EphemeralForEachAsync_WithKeySelector_ProcessesSequentiallyPerKey()
    {
        var items = new[]
        {
            (Key: "A", Value: 1),
            (Key: "B", Value: 1),
            (Key: "A", Value: 2),
            (Key: "B", Value: 2)
        };
        var order = new List<(string Key, int Value)>();
        var lockObj = new object();

        await items.EphemeralForEachAsync(
            x => x.Key,
            async (item, ct) =>
            {
                await Task.Delay(20, ct);
                lock (lockObj)
                {
                    order.Add(item);
                }
            },
            new EphemeralOptions { MaxConcurrency = 4, MaxConcurrencyPerKey = 1 });

        // Items for same key should be in order
        var aItems = order.Where(x => x.Key == "A").Select(x => x.Value).ToList();
        var bItems = order.Where(x => x.Key == "B").Select(x => x.Value).ToList();

        Assert.Equal(new[] { 1, 2 }, aItems);
        Assert.Equal(new[] { 1, 2 }, bItems);
    }
}