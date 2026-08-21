using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

/// <summary>
///     NOTE on time injection: these hung-body tests use a short REAL bound (TimeProvider.System)
///     rather than FakeTimeProvider. Verified independently that (a) FakeTimeProvider + Task.WaitAsync
///     works correctly in isolation, and (b) this bound's real-clock path works correctly through the
///     full coordinator — but FakeTimeProvider combined with the coordinator's background-task
///     threading model hung even after fixing the obvious advance/registration race (repeated
///     Advance() calls instead of one). Root cause unresolved; using a short (200ms) real bound is a
///     pragmatic, proven-reliable substitute, not a silent abandonment of the injected-time rule —
///     flagged for follow-up investigation.
/// </summary>
public class BodyDurationBoundTests
{
    private const string TimeoutSignal = "coordinator.body.timeout";
    private static readonly TimeSpan ShortRealBound = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void Constructor_RejectsZeroMaxBodyDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EphemeralWorkCoordinator<int>((item, ct) => Task.CompletedTask, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_RejectsNegativeMaxBodyDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EphemeralWorkCoordinator<int>((item, ct) => Task.CompletedTask, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task BodyCompletingWithinBound_Succeeds()
    {
        // FakeTimeProvider is safe here: the test never calls Advance(), so there's no
        // registration race to hit — it's just a clock reference the body finishes well under.
        var timeProvider = new FakeTimeProvider();
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(1, ct),
            TimeSpan.FromSeconds(5),
            new EphemeralOptions { MaxConcurrency = 1 },
            timeProvider);

        await coordinator.EnqueueAsync(1);
        coordinator.Complete();
        await coordinator.DrainAsync();

        Assert.Equal(1, coordinator.TotalCompleted);
        Assert.Equal(0, coordinator.TotalFailed);
    }

    [Fact]
    public async Task HungBody_TripsBound_FreesSlot_RecordsBodyDurationExceeded_EmitsLoudSignal()
    {
        var sink = new SignalSink();
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                if (item == 1)
                {
                    bodyStarted.TrySetResult();
                    // Never completes on its own — simulates the "hung body" failure class this
                    // bound exists to survive. Not cancellation-aware, matching the real-world
                    // incident (a body that never checks its token).
                    await release.Task.ConfigureAwait(false);
                }
            },
            ShortRealBound,
            new EphemeralOptions { MaxConcurrency = 1, Signals = sink });

        await coordinator.EnqueueAsync(1);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The hung body holds the coordinator's only concurrency slot.
        Assert.Equal(1, coordinator.ActiveCount);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (coordinator.ActiveCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(1, coordinator.TotalFailed);

        var failed = Assert.Single(coordinator.GetFailed());
        Assert.IsType<BodyDurationExceededException>(failed.Error);
        Assert.True(failed.HasSignal(TimeoutSignal), "trip must be loud: expected the timeout signal on the op");
        Assert.Contains(sink.Sense(), e => e.Signal == TimeoutSignal);

        // The freed slot must be usable by the next item — a hang must not destroy the coordinator.
        await coordinator.EnqueueAsync(2);
        coordinator.Complete();
        await coordinator.DrainAsync();
        Assert.Equal(1, coordinator.TotalCompleted);

        release.TrySetResult(); // let the orphaned body unwind; nothing observes it anymore
    }

    [Fact]
    public async Task HungBody_InKeyedCoordinator_TripsBound_FreesKeyLock()
    {
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new EphemeralKeyedWorkCoordinator<int, string>(
            _ => "same-key",
            async (item, ct) =>
            {
                if (item == 1)
                {
                    bodyStarted.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                }
            },
            ShortRealBound,
            new EphemeralOptions { MaxConcurrencyPerKey = 1 });

        await coordinator.EnqueueAsync(1);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (coordinator.ActiveCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, coordinator.TotalFailed);

        // Same key, next item — proves the per-key lock was released, not just the global gate.
        await coordinator.EnqueueAsync(2);
        coordinator.Complete();
        await coordinator.DrainAsync();
        Assert.Equal(1, coordinator.TotalCompleted);

        release.TrySetResult();
    }

    [Fact]
    public async Task HungBody_InResultCoordinator_TripsBound_RecordsFailure()
    {
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new EphemeralResultCoordinator<int, int>(
            async (item, ct) =>
            {
                bodyStarted.TrySetResult();
                return await release.Task.ConfigureAwait(false);
            },
            ShortRealBound,
            new EphemeralOptions { MaxConcurrency = 1 });

        await coordinator.EnqueueAsync(1);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (coordinator.ActiveCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, coordinator.TotalFailed);
        var failed = Assert.Single(coordinator.GetFailed());
        Assert.IsType<BodyDurationExceededException>(failed.Error);

        release.TrySetResult(0);
    }
}
