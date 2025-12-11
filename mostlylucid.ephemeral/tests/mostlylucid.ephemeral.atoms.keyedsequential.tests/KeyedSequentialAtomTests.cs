using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;

namespace Mostlylucid.Ephemeral.Atoms.KeyedSequential.Tests;

public class KeyedSequentialAtomTests
{
    [Fact]
    public async Task EnqueueAsync_ProcessesItemsSequentiallyPerKey()
    {
        // Arrange
        var processed = new List<(string Key, int Value, DateTime Time)>();
        var atom = new KeyedSequentialAtom<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                lock (processed) processed.Add((item.Key, item.Value, DateTime.UtcNow));
            },
            maxConcurrency: 4,
            perKeyConcurrency: 1);

        // Act
        await atom.EnqueueAsync(("A", 1));
        await atom.EnqueueAsync(("A", 2));
        await atom.EnqueueAsync(("B", 1));
        await atom.EnqueueAsync(("A", 3));
        await atom.DrainAsync();

        // Assert
        await atom.DisposeAsync();

        var keyA = processed.Where(p => p.Key == "A").Select(p => p.Value).ToList();
        var keyB = processed.Where(p => p.Key == "B").Select(p => p.Value).ToList();

        Assert.Equal(new[] { 1, 2, 3 }, keyA); // Sequential for key A
        Assert.Equal(new[] { 1 }, keyB);
    }

    [Fact]
    public async Task ParallelProcessing_AcrossDifferentKeys()
    {
        // Arrange
        var activeKeys = new HashSet<string>();
        var maxParallel = 0;
        var lockObj = new object();

        var atom = new KeyedSequentialAtom<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) =>
            {
                lock (lockObj)
                {
                    activeKeys.Add(item.Key);
                    maxParallel = Math.Max(maxParallel, activeKeys.Count);
                }

                await Task.Delay(50, ct);

                lock (lockObj)
                {
                    activeKeys.Remove(item.Key);
                }
            },
            maxConcurrency: 4);

        // Act
        var tasks = new[]
        {
            atom.EnqueueAsync(("A", 1)).AsTask(),
            atom.EnqueueAsync(("B", 1)).AsTask(),
            atom.EnqueueAsync(("C", 1)).AsTask(),
            atom.EnqueueAsync(("D", 1)).AsTask()
        };
        await Task.WhenAll(tasks);
        await atom.DrainAsync();

        // Assert
        Assert.True(maxParallel > 1, "Should process different keys in parallel");
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task Stats_ReturnsCorrectCounts()
    {
        // Arrange
        var gate = new SemaphoreSlim(0);
        var atom = new KeyedSequentialAtom<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) => await gate.WaitAsync(ct),
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(("A", 1));
        await atom.EnqueueAsync(("B", 1));
        await Task.Delay(50);

        var stats = atom.Stats();

        // Assert
        Assert.True(stats.Pending + stats.Active >= 1);

        // Cleanup
        gate.Release(10);
        await atom.DrainAsync();
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task Snapshot_ReturnsOperations()
    {
        // Arrange
        var gate = new SemaphoreSlim(0);
        var atom = new KeyedSequentialAtom<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) => await gate.WaitAsync(ct),
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(("A", 1));
        await Task.Delay(50);

        var snapshot = atom.Snapshot();

        // Assert
        Assert.NotEmpty(snapshot);

        // Cleanup
        gate.Release(10);
        await atom.DrainAsync();
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task FairScheduling_PreventsStarvation()
    {
        // Arrange
        var processed = new List<string>();
        var atom = new KeyedSequentialAtom<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) =>
            {
                await Task.Delay(5, ct);
                lock (processed) processed.Add($"{item.Key}:{item.Value}");
            },
            maxConcurrency: 2,
            enableFairScheduling: true);

        // Act - Enqueue many items for key A, then some for key B
        for (int i = 0; i < 10; i++)
            await atom.EnqueueAsync(("A", i));

        for (int i = 0; i < 3; i++)
            await atom.EnqueueAsync(("B", i));

        await atom.DrainAsync();

        // Assert - Key B should get processed, not starved by key A
        await atom.DisposeAsync();
        var keyBProcessed = processed.Count(p => p.StartsWith("B:"));
        Assert.Equal(3, keyBProcessed);
    }
}
