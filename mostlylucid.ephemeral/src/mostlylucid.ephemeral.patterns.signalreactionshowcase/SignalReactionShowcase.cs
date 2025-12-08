namespace Mostlylucid.Ephemeral.Patterns.SignalReactionShowcase;

/// <summary>
///     Demonstrates raising signals inside work items, reacting immediately (OnSignal), and polling the sink for patterns.
/// </summary>
public static class SignalReactionShowcase
{
    public static async Task<Result> RunAsync(int itemCount = 8, CancellationToken ct = default)
    {
        if (itemCount <= 0) throw new ArgumentOutOfRangeException(nameof(itemCount));

        // Global sink we can poll later; bounded so it cannot balloon.
        var sink = new SignalSink(itemCount * 4, TimeSpan.FromSeconds(10));
        var dispatchedHits = 0;

        // Async fan-out so signal handling stays off the hot path.
        await using var dispatcher = new SignalDispatcher(new EphemeralOptions
            { MaxTrackedOperations = itemCount * 4, MaxConcurrency = 4 });
        dispatcher.Register("stage.done:*", evt =>
        {
            Interlocked.Increment(ref dispatchedHits);
            return Task.CompletedTask;
        });

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, token) =>
            {
                // Emit immediately when work starts; this is synchronous but tiny.
                var start = new SignalEvent($"stage.start:{item}", EphemeralIdGenerator.NextId(), null,
                    DateTimeOffset.UtcNow);
                sink.Raise(start);
                dispatcher.Dispatch(start);

                await Task.Delay(5, token).ConfigureAwait(false);

                // Emit completion and fan it out; downstream stays async.
                var done = new SignalEvent($"stage.done:{item}", EphemeralIdGenerator.NextId(), null,
                    DateTimeOffset.UtcNow);
                sink.Raise(done);
                dispatcher.Dispatch(done);
            },
            new EphemeralOptions
            {
                MaxConcurrency = Math.Min(Environment.ProcessorCount, 8)
            });

        for (var i = 0; i < itemCount; i++)
            await coordinator.EnqueueAsync(i, ct).ConfigureAwait(false);

        coordinator.Complete();
        await coordinator.DrainAsync(ct).ConfigureAwait(false);
        await dispatcher.FlushAsync(ct).ConfigureAwait(false);

        var polledHits = sink.Sense(s => s.Signal.StartsWith("stage.done", StringComparison.Ordinal)).Count;
        var signals = sink.Sense().Select(s => s.Signal).ToArray();

        return new Result(dispatchedHits, polledHits, signals);
    }

    public sealed record Result(int DispatchedHits, int PolledHits, IReadOnlyList<string> Signals);
}