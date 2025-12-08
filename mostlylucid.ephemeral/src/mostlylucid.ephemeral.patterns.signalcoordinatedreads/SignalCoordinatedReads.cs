namespace Mostlylucid.Ephemeral.Patterns.SignalCoordinatedReads;

/// <summary>
///     Demonstrates readers that pause when an update signal is present, then resume when cleared.
///     Useful for brief "quiesce reads while config/migration runs" patterns without hard locks.
/// </summary>
public static class SignalCoordinatedReads
{
    private const string UpdateSignal = "update.in-progress";
    private const string UpdateClearedSignal = "update.done";

    public static async Task<Result> RunAsync(int readCount = 10, int updateCount = 1, CancellationToken ct = default)
    {
        var sink = new SignalSink(128, TimeSpan.FromSeconds(5));
        var reads = 0;
        var updates = 0;

        // Readers defer on the update signal; the updater emits it and then clears it.
        await using var readerCoordinator = new EphemeralWorkCoordinator<int>(
            async (_, token) =>
            {
                // Wait out defer signals before proceeding.
                sink.Raise(new SignalEvent("read.waiting", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
                await Task.Delay(5, token).ConfigureAwait(false);
                Interlocked.Increment(ref reads);
            },
            new EphemeralOptions
            {
                MaxConcurrency = 4,
                Signals = sink,
                DeferOnSignals = new HashSet<string> { UpdateSignal },
                DeferCheckInterval = TimeSpan.FromMilliseconds(20),
                MaxDeferAttempts = 500
            });

        await using var updateCoordinator = new EphemeralWorkCoordinator<int>(
            async (_, token) =>
            {
                // Emit update signal, run work, then clear by emitting a "done" marker.
                sink.Raise(new SignalEvent(UpdateSignal, EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
                await Task.Delay(50, token).ConfigureAwait(false);
                sink.Raise(new SignalEvent(UpdateClearedSignal, EphemeralIdGenerator.NextId(), null,
                    DateTimeOffset.UtcNow));
                Interlocked.Increment(ref updates);
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                Signals = sink
            });

        // Enqueue reads and updates interleaved.
        for (var i = 0; i < readCount; i++)
            await readerCoordinator.EnqueueAsync(i, ct).ConfigureAwait(false);

        for (var i = 0; i < updateCount; i++)
            await updateCoordinator.EnqueueAsync(i, ct).ConfigureAwait(false);

        readerCoordinator.Complete();
        updateCoordinator.Complete();

        await updateCoordinator.DrainAsync(ct).ConfigureAwait(false);
        await readerCoordinator.DrainAsync(ct).ConfigureAwait(false);

        var signals = sink.Sense().Select(s => s.Signal).ToArray();
        return new Result(reads, updates, signals);
    }

    public sealed record Result(int ReadsCompleted, int UpdatesCompleted, IReadOnlyList<string> Signals);
}