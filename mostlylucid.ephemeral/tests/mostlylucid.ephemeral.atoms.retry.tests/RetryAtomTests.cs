using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Retry;

namespace Mostlylucid.Ephemeral.Atoms.Retry.Tests;

public class RetryAtomTests
{
    [Fact]
    public async Task EnqueueAsync_RetriesOnFailure()
    {
        // Arrange
        var attemptCounts = new Dictionary<int, int>();
        var lockObj = new object();

        var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(5, ct);
                lock (lockObj)
                {
                    attemptCounts.TryGetValue(item, out var count);
                    attemptCounts[item] = count + 1;

                    if (attemptCounts[item] < 3)
                        throw new InvalidOperationException("Simulated failure");
                }
            },
            maxAttempts: 3,
            backoff: attempt => TimeSpan.FromMilliseconds(10));

        // Act
        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        // Assert
        await atom.DisposeAsync();
        Assert.Equal(3, attemptCounts[1]);
    }

    [Fact]
    public async Task EnqueueAsync_SucceedsOnFirstAttempt()
    {
        // Arrange
        var processed = new List<int>();
        var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(5, ct);
                lock (processed) processed.Add(item);
            },
            maxAttempts: 3);

        // Act
        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        // Assert
        await atom.DisposeAsync();
        Assert.Single(processed);
    }

    [Fact]
    public async Task EnqueueAsync_GivesUpAfterMaxAttempts()
    {
        // Arrange
        var attempts = 0;
        var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(5, ct);
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("Always fails");
            },
            maxAttempts: 3,
            backoff: _ => TimeSpan.FromMilliseconds(5));

        // Act
        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        // Assert - RetryAtom exhausts retries then lets exception propagate to coordinator
        // The operation will be marked as failed
        var stats = atom.Stats();
        await atom.DisposeAsync();

        Assert.Equal(3, attempts); // Tried 3 times
        Assert.Equal(1, stats.Failed); // Marked as failed after exhausting retries
    }

    [Fact]
    public async Task Backoff_IncreasesDelay()
    {
        // Arrange
        var attemptTimes = new List<DateTime>();
        var lockObj = new object();

        var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(1, ct);
                lock (lockObj)
                {
                    attemptTimes.Add(DateTime.UtcNow);
                    if (attemptTimes.Count < 3)
                        throw new InvalidOperationException("Retry me");
                }
            },
            maxAttempts: 3,
            backoff: attempt => TimeSpan.FromMilliseconds(attempt * 50));

        // Act
        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        // Assert
        await atom.DisposeAsync();
        Assert.Equal(3, attemptTimes.Count);

        // Verify delays increased
        var delay1 = attemptTimes[1] - attemptTimes[0];
        var delay2 = attemptTimes[2] - attemptTimes[1];
        Assert.True(delay2 > delay1, $"Second delay ({delay2.TotalMilliseconds}ms) should be > first ({delay1.TotalMilliseconds}ms)");
    }

    [Fact]
    public async Task Stats_TracksRetries()
    {
        // Arrange
        var failCount = 0;
        var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(5, ct);
                if (Interlocked.Increment(ref failCount) < 2)
                    throw new InvalidOperationException("Fail once");
            },
            maxAttempts: 3);

        // Act
        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        var stats = atom.Stats();

        // Assert
        Assert.Equal(1, stats.Completed);
        await atom.DisposeAsync();
    }

    [Fact]
    public async Task MultipleItems_AllRetry()
    {
        // Arrange
        var attempts = new Dictionary<int, int>();
        var lockObj = new object();

        var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(5, ct);
                lock (lockObj)
                {
                    attempts.TryGetValue(item, out var count);
                    attempts[item] = count + 1;

                    if (attempts[item] < 2)
                        throw new InvalidOperationException($"Retry item {item}");
                }
            },
            maxAttempts: 3,
            maxConcurrency: 2);

        // Act
        await atom.EnqueueAsync(1);
        await atom.EnqueueAsync(2);
        await atom.EnqueueAsync(3);
        await atom.DrainAsync();

        // Assert
        await atom.DisposeAsync();
        Assert.All(attempts.Values, count => Assert.Equal(2, count));
    }
}
