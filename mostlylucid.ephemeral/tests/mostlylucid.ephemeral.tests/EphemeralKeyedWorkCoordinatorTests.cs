using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class EphemeralKeyedWorkCoordinatorTests
{
    [Fact]
    public async Task EnqueueAsync_ProcessesItemsSequentiallyPerKey()
    {
        var order = new List<(string Key, int Value)>();
        var lockObj = new object();

        await using var coordinator = new EphemeralKeyedWorkCoordinator<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) =>
            {
                await Task.Delay(20, ct);
                lock (lockObj)
                {
                    order.Add(item);
                }
            },
            new EphemeralOptions { MaxConcurrency = 4 });

        // Queue items for two different keys
        await coordinator.EnqueueAsync(("A", 1));
        await coordinator.EnqueueAsync(("B", 1));
        await coordinator.EnqueueAsync(("A", 2));
        await coordinator.EnqueueAsync(("B", 2));
        await coordinator.EnqueueAsync(("A", 3));
        await coordinator.EnqueueAsync(("B", 3));

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Verify items for same key are processed in order
        var aItems = order.Where(x => x.Key == "A").Select(x => x.Value).ToList();
        var bItems = order.Where(x => x.Key == "B").Select(x => x.Value).ToList();

        Assert.Equal(new[] { 1, 2, 3 }, aItems);
        Assert.Equal(new[] { 1, 2, 3 }, bItems);
    }

    [Fact]
    public async Task DifferentKeys_ProcessInParallel()
    {
        var activeKeys = new HashSet<string>();
        var maxConcurrentKeys = 0;
        var lockObj = new object();

        await using var coordinator = new EphemeralKeyedWorkCoordinator<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) =>
            {
                lock (lockObj)
                {
                    activeKeys.Add(item.Key);
                    if (activeKeys.Count > maxConcurrentKeys)
                        maxConcurrentKeys = activeKeys.Count;
                }

                await Task.Delay(50, ct);
                lock (lockObj)
                {
                    activeKeys.Remove(item.Key);
                }
            },
            new EphemeralOptions { MaxConcurrency = 4 });

        await coordinator.EnqueueAsync(("A", 1));
        await coordinator.EnqueueAsync(("B", 1));
        await coordinator.EnqueueAsync(("C", 1));
        await coordinator.EnqueueAsync(("D", 1));

        coordinator.Complete();
        await coordinator.DrainAsync();

        Assert.True(maxConcurrentKeys > 1, "Expected parallel execution of different keys");
    }
}