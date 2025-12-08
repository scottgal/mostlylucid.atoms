namespace Mostlylucid.Ephemeral.Patterns.LongWindowDemo;

/// <summary>
///     Demonstrates that the Ephemeral window can be short (tiny memory) or long (thousands of operations)
///     while staying bounded by MaxTrackedOperations.
/// </summary>
public static class LongWindowDemo
{
    public static async Task<Result> RunAsync(int totalItems, int windowSize, int workDelayMs = 0,
        CancellationToken ct = default)
    {
        if (totalItems < 0) throw new ArgumentOutOfRangeException(nameof(totalItems));
        if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));

        var options = new EphemeralOptions
        {
            MaxTrackedOperations = windowSize,
            MaxConcurrency = Math.Min(Environment.ProcessorCount, 8)
        };

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (_, token) =>
            {
                if (workDelayMs > 0)
                    await Task.Delay(workDelayMs, token).ConfigureAwait(false);
            },
            options);

        for (var i = 0; i < totalItems; i++)
            await coordinator.EnqueueAsync(i, ct).ConfigureAwait(false);

        coordinator.Complete();
        await coordinator.DrainAsync(ct).ConfigureAwait(false);

        var tracked = coordinator.GetSnapshot().Count;
        return new Result(windowSize, totalItems, tracked);
    }

    public readonly record struct Result(int WindowSize, int TotalItems, int TrackedCount);
}