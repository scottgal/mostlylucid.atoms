using System.Diagnostics;
using System.Linq;
using Mostlylucid.Helpers.Ephemeral;
using Xunit;

namespace Mostlylucid.Test.Ephemeral;

public class WorkCoordinatorRegressionTests
{
    [Fact]
    public async Task EnqueueWithIdAsync_UsesReturnedId()
    {
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            (_, _) => Task.CompletedTask,
            new EphemeralOptions
            {
                MaxConcurrency = 2,
                MaxTrackedOperations = 4
            });

        var id = await coordinator.EnqueueWithIdAsync(1);
        coordinator.Complete();
        await coordinator.DrainAsync();

        var snapshot = coordinator.GetSnapshot();
        var op = Assert.Single(snapshot);
        Assert.Equal(id, op.Id);
    }

    [Fact]
    public async Task PinnedHead_DoesNotBlockSizeEviction()
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 2
        };

        await using var coordinator = new EphemeralWorkCoordinator<int>((_, _) => Task.CompletedTask, options);

        var pinnedId = await coordinator.EnqueueWithIdAsync(1);
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 1));
        Assert.True(coordinator.Pin(pinnedId));

        await coordinator.EnqueueWithIdAsync(2);
        await coordinator.EnqueueWithIdAsync(3);
        coordinator.Complete();
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 3));
        await coordinator.DrainAsync();

        var snapshot = coordinator.GetSnapshot();
        Assert.Contains(snapshot, s => s.Id == pinnedId);
        Assert.True(snapshot.Count <= options.MaxTrackedOperations, $"Expected window <= {options.MaxTrackedOperations} but was {snapshot.Count}");
    }

    [Fact]
    public async Task AgeEviction_RemovesStaleNonPinnedEvenWhenPinnedIsOldest()
    {
        var lifetime = TimeSpan.FromMilliseconds(30);
        var options = new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 2,
            MaxOperationLifetime = lifetime
        };

        await using var coordinator = new EphemeralWorkCoordinator<int>((_, _) => Task.CompletedTask, options);

        var pinnedId = await coordinator.EnqueueWithIdAsync(1);
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 1));
        coordinator.Pin(pinnedId);

        await Task.Delay(lifetime * 6);

        var staleId = await coordinator.EnqueueWithIdAsync(2);
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 2));

        await Task.Delay(lifetime * 6);

        await coordinator.EnqueueWithIdAsync(3);
        coordinator.Complete();
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted >= 3));
        await coordinator.DrainAsync();

        var snapshot = coordinator.GetSnapshot();
        Assert.Contains(snapshot, s => s.Id == pinnedId);
        Assert.DoesNotContain(snapshot, s => s.Id == staleId);
    }

    [Fact]
    public async Task LargeLoad_ProcessesAllAndKeepsWindowBounded()
    {
        const int itemCount = 1000;
        var processed = 0;
        var maxObservedActive = 0;
        var currentActive = 0;

        var options = new EphemeralOptions
        {
            MaxConcurrency = 16,
            MaxTrackedOperations = 64,
            EnableDynamicConcurrency = true
        };

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            (_, _) =>
            {
                var active = Interlocked.Increment(ref currentActive);
                InterlockedExtensions.UpdateMax(ref maxObservedActive, active);
                Interlocked.Increment(ref processed);
                Interlocked.Decrement(ref currentActive);
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
        Assert.True(maxObservedActive <= options.MaxConcurrency, $"Observed active {maxObservedActive} exceeded limit {options.MaxConcurrency}");
        var snapshot = coordinator.GetSnapshot();
        Assert.True(snapshot.Count <= options.MaxTrackedOperations, $"Expected window <= {options.MaxTrackedOperations} but was {snapshot.Count}");
        Assert.DoesNotContain(snapshot, s => s.Error is not null);
    }

    [Fact]
    public async Task DynamicConcurrency_Increase_AllowsMoreParallelism()
    {
        const int itemCount = 200;
        var maxObservedActive = 0;
        var currentActive = 0;

        var options = new EphemeralOptions
        {
            MaxConcurrency = 2,
            MaxTrackedOperations = 64,
            EnableDynamicConcurrency = true
        };

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (_, ct) =>
            {
                var active = Interlocked.Increment(ref currentActive);
                InterlockedExtensions.UpdateMax(ref maxObservedActive, active);
                await Task.Delay(10, ct);
                Interlocked.Decrement(ref currentActive);
            },
            options);

        for (var i = 0; i < itemCount; i++)
        {
            await coordinator.EnqueueAsync(i);
        }

        // Increase concurrency after some work has started
        await Task.Delay(50);
        coordinator.SetMaxConcurrency(8);

        coordinator.Complete();
        Assert.True(await WaitForAsync(() => coordinator.TotalCompleted + coordinator.TotalFailed == itemCount, TimeSpan.FromSeconds(5)));
        await coordinator.DrainAsync();

        Assert.Equal(itemCount, coordinator.TotalCompleted);
        Assert.True(maxObservedActive >= 4, $"Expected dynamic increase to raise active count; observed {maxObservedActive}");
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

    private static class InterlockedExtensions
    {
        public static void UpdateMax(ref int target, int value)
        {
            int initial, computed;
            do
            {
                initial = target;
                computed = Math.Max(initial, value);
                if (computed == initial)
                    return;
            } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
        }
    }
}
