using System.Diagnostics;
using System.Linq;
using Mostlylucid.Helpers.Ephemeral;
using Xunit;

namespace Mostlylucid.Test.Ephemeral;

public class KeyedCoordinatorRegressionTests
{
    [Fact]
    public async Task PinnedHead_DoesNotBlockSizeEviction()
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 2
        };

        await using var coordinator = new EphemeralKeyedWorkCoordinator<int, int>(
            i => i,
            (_, _) => Task.CompletedTask,
            options);

        await coordinator.EnqueueAsync(1);
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 1));
        var pinnedId = coordinator.GetSnapshot().First().Id;
        coordinator.Pin(pinnedId);

        await coordinator.EnqueueAsync(2);
        await coordinator.EnqueueAsync(3);
        coordinator.Complete();
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 3));
        await coordinator.DrainAsync();

        var snapshot = coordinator.GetSnapshot();
        Assert.Contains(snapshot, s => s.Id == pinnedId);
        Assert.True(snapshot.Count <= options.MaxTrackedOperations, $"Expected window <= {options.MaxTrackedOperations} but was {snapshot.Count}");
    }

    [Fact]
    public async Task LargeLoad_ProcessesAllAndKeepsWindowBounded()
    {
        const int itemCount = 1000;
        var processed = 0;

        var options = new EphemeralOptions
        {
            MaxConcurrency = 16,
            MaxConcurrencyPerKey = 2,
            MaxTrackedOperations = 64,
            EnableDynamicConcurrency = true
        };

        await using var coordinator = new EphemeralKeyedWorkCoordinator<int, int>(
            i => i % 10,
            (_, _) =>
            {
                Interlocked.Increment(ref processed);
                return Task.CompletedTask;
            },
            options);

        for (var i = 0; i < itemCount; i++)
        {
            await coordinator.EnqueueAsync(i);
        }

        coordinator.Complete();
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted + coordinator.TotalFailed == itemCount, TimeSpan.FromSeconds(5)));
        await coordinator.DrainAsync();

        Assert.Equal(itemCount, processed);
        Assert.Equal(itemCount, coordinator.TotalCompleted);
        Assert.Equal(0, coordinator.TotalFailed);
        var snapshot = coordinator.GetSnapshot();
        Assert.True(snapshot.Count <= options.MaxTrackedOperations, $"Expected window <= {options.MaxTrackedOperations} but was {snapshot.Count}");
        Assert.DoesNotContain(snapshot, s => s.Error is not null);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null, int delayMilliseconds = 10)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;

            await Task.Delay(delayMilliseconds);
        }

        return condition();
    }
}
