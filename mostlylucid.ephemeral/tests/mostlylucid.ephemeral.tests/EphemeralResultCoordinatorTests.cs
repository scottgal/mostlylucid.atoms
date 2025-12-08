using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class EphemeralResultCoordinatorTests
{
    [Fact]
    public async Task EnqueueAsync_ReturnsResultWhenComplete()
    {
        await using var coordinator = new EphemeralResultCoordinator<int, string>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                return $"Result:{item}";
            },
            new EphemeralOptions { MaxConcurrency = 2 });

        await coordinator.EnqueueAsync(42);

        coordinator.Complete();
        await coordinator.DrainAsync();

        var results = coordinator.GetResults();
        Assert.Single(results);
        Assert.Equal("Result:42", results.First());
    }

    [Fact]
    public async Task MultipleItems_AllReturnResults()
    {
        await using var coordinator = new EphemeralResultCoordinator<int, int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                return item * 2;
            },
            new EphemeralOptions { MaxConcurrency = 4 });

        for (var i = 1; i <= 5; i++)
            await coordinator.EnqueueAsync(i);

        coordinator.Complete();
        await coordinator.DrainAsync();

        var results = coordinator.GetResults();
        Assert.Equal(5, results.Count);
        Assert.Contains(2, results);
        Assert.Contains(4, results);
        Assert.Contains(6, results);
        Assert.Contains(8, results);
        Assert.Contains(10, results);
    }

    [Fact]
    public async Task GetSuccessful_ReturnsOnlySuccessfulOperations()
    {
        await using var coordinator = new EphemeralResultCoordinator<int, int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                if (item == 2) throw new Exception("Test error");
                return item * 2;
            },
            new EphemeralOptions { MaxConcurrency = 4 });

        await coordinator.EnqueueAsync(1);
        await coordinator.EnqueueAsync(2);
        await coordinator.EnqueueAsync(3);

        coordinator.Complete();
        await coordinator.DrainAsync();

        var successful = coordinator.GetSuccessful();
        Assert.Equal(2, successful.Count);

        var failed = coordinator.GetFailed();
        Assert.Single(failed);
    }
}