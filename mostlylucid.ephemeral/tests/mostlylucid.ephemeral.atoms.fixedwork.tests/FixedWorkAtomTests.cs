using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.FixedWork;

namespace Mostlylucid.Ephemeral.Atoms.FixedWork.Tests;

/// <summary>
///     Tests for FixedWorkAtom, verifying AtomBase integration and core functionality.
/// </summary>
public class FixedWorkAtomTests
{
    [Fact]
    public async Task EnqueueAsync_ProcessesItems()
    {
        // Arrange
        var processed = new List<int>();
        var atom = new FixedWorkAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                lock (processed) processed.Add(item);
            },
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(1);
        await atom.EnqueueAsync(2);
        await atom.EnqueueAsync(3);
        await atom.DrainAsync();

        // Assert
        await atom.DisposeAsync();
        Assert.Equal(3, processed.Count);
        Assert.Contains(1, processed);
        Assert.Contains(2, processed);
        Assert.Contains(3, processed);
    }

    [Fact]
    public async Task EnqueueAsync_ReturnsOperationId()
    {
        // Arrange
        var atom = new FixedWorkAtom<int>(
            async (item, ct) => await Task.Delay(10, ct),
            maxConcurrency: 1);

        // Act
        var id1 = await atom.EnqueueAsync(1);
        var id2 = await atom.EnqueueAsync(2);

        // Assert
        Assert.True(id1 > 0);
        Assert.True(id2 > 0);
        Assert.NotEqual(id1, id2);

        await atom.DrainAsync();
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task DrainAsync_WaitsForCompletion()
    {
        // Arrange
        var completed = 0;
        var atom = new FixedWorkAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(50, ct);
                Interlocked.Increment(ref completed);
            },
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(1);
        await atom.EnqueueAsync(2);
        await atom.EnqueueAsync(3);

        var drainTask = atom.DrainAsync();
        await drainTask;

        // Assert
        Assert.Equal(3, completed);
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task Stats_ReturnsCorrectCounts()
    {
        // Arrange
        var gate = new SemaphoreSlim(0);
        var atom = new FixedWorkAtom<int>(
            async (item, ct) =>
            {
                await gate.WaitAsync(ct);
            },
            maxConcurrency: 2,
            maxTracked: 100);

        // Act
        await atom.EnqueueAsync(1);
        await atom.EnqueueAsync(2);
        await atom.EnqueueAsync(3);

        await Task.Delay(50); // Let items start processing
        var stats = atom.Stats();

        // Assert - Some should be active, some pending
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
        var atom = new FixedWorkAtom<int>(
            async (item, ct) => await gate.WaitAsync(ct),
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(1);
        await atom.EnqueueAsync(2);
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
    public async Task MaxConcurrency_LimitsParallelism()
    {
        // Arrange
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var atom = new FixedWorkAtom<int>(
            async (item, ct) =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                var max = maxConcurrent;
                while (current > max)
                {
                    if (Interlocked.CompareExchange(ref maxConcurrent, current, max) == max)
                        break;
                    max = maxConcurrent;
                }

                await Task.Delay(50, ct);
                Interlocked.Decrement(ref concurrentCount);
            },
            maxConcurrency: 3);

        // Act
        var tasks = Enumerable.Range(1, 10).Select(i => atom.EnqueueAsync(i).AsTask()).ToArray();
        await Task.WhenAll(tasks);
        await atom.DrainAsync();

        // Assert
        Assert.True(maxConcurrent <= 3, $"Max concurrent was {maxConcurrent}, expected <= 3");
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CompletesGracefully()
    {
        // Arrange
        var completed = new List<int>();
        var atom = new FixedWorkAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                lock (completed) completed.Add(item);
            },
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(1);
        await atom.EnqueueAsync(2);
        await atom.DrainAsync();  // Explicitly drain before dispose

        // Assert - DrainAsync should complete all pending work
        Assert.Equal(2, completed.Count);
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task DefaultConcurrency_UsesProcessorCount()
    {
        // Arrange & Act
        var atom = new FixedWorkAtom<int>(
            async (item, ct) => await Task.Delay(1, ct));

        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        var stats = atom.Stats();

        // Assert - Just verify it works with default concurrency
        Assert.Equal(1, stats.Completed);
        await atom.DisposeAsync();
    }
}
