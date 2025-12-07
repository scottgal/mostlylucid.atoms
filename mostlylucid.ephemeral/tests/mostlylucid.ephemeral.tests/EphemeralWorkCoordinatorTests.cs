using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class EphemeralWorkCoordinatorTests
{
    [Fact]
    public async Task EnqueueAsync_ProcessesItems()
    {
        var processed = new List<int>();
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                lock (processed) processed.Add(item);
                await Task.Delay(10, ct);
            },
            new EphemeralOptions { MaxConcurrency = 2 });

        await coordinator.EnqueueAsync(1);
        await coordinator.EnqueueAsync(2);
        await coordinator.EnqueueAsync(3);

        coordinator.Complete();
        await coordinator.DrainAsync();

        Assert.Equal(3, processed.Count);
        Assert.Contains(1, processed);
        Assert.Contains(2, processed);
        Assert.Contains(3, processed);
    }

    [Fact]
    public async Task MaxConcurrency_LimitsConcurrentExecution()
    {
        var running = 0;
        var maxRunning = 0;
        var lockObj = new object();

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                lock (lockObj)
                {
                    running++;
                    if (running > maxRunning) maxRunning = running;
                }
                await Task.Delay(50, ct);
                lock (lockObj) running--;
            },
            new EphemeralOptions { MaxConcurrency = 2 });

        for (var i = 0; i < 10; i++)
            await coordinator.EnqueueAsync(i);

        coordinator.Complete();
        await coordinator.DrainAsync();

        Assert.True(maxRunning <= 2, $"Max running was {maxRunning}, expected <= 2");
    }

    [Fact]
    public async Task GetSnapshot_ReturnsOperationStates()
    {
        var tcs = new TaskCompletionSource();
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await tcs.Task,
            new EphemeralOptions { MaxConcurrency = 2 });

        await coordinator.EnqueueAsync(1);
        await coordinator.EnqueueAsync(2);

        await Task.Delay(50);

        var snapshot = coordinator.GetSnapshot();
        Assert.NotEmpty(snapshot);

        tcs.SetResult();
        coordinator.Complete();
        await coordinator.DrainAsync();
    }

    [Fact]
    public async Task SetMaxConcurrency_AdjustsConcurrencyDynamically()
    {
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (_, ct) => await Task.Delay(10, ct),
            new EphemeralOptions { MaxConcurrency = 1, EnableDynamicConcurrency = true });

        Assert.Equal(1, coordinator.CurrentMaxConcurrency);

        coordinator.SetMaxConcurrency(4);
        Assert.Equal(4, coordinator.CurrentMaxConcurrency);

        coordinator.SetMaxConcurrency(2);
        Assert.Equal(2, coordinator.CurrentMaxConcurrency);
    }

    [Fact]
    public async Task PauseResume_ControlsProcessing()
    {
        var processed = new List<int>();
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                lock (processed) processed.Add(item);
                await Task.Delay(10, ct);
            },
            new EphemeralOptions { MaxConcurrency = 1 });

        await coordinator.EnqueueAsync(1);
        await Task.Delay(50);

        coordinator.Pause();
        Assert.True(coordinator.IsPaused);

        coordinator.Resume();
        Assert.False(coordinator.IsPaused);

        coordinator.Complete();
        await coordinator.DrainAsync();
    }
}
