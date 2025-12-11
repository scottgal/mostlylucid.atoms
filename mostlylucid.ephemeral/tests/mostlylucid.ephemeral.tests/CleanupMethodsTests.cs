using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

/// <summary>
/// Tests for the new cleanup methods on AtomBase and SignalSink.
/// </summary>
public class CleanupMethodsTests
{
    #region AtomBase Cleanup Tests

    [Fact]
    public async Task Atom_Cleanup_TimeSpan_RemovesOldSignals()
    {
        var sink = new SignalSink();
        await using var atom = new TestAtom(sink);

        // Create some operations with signals
        for (int i = 0; i < 5; i++)
        {
            var opId = await atom.EnqueueAsync(i);
            sink.Raise(new SignalEvent($"test.{i}", opId, null, DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        await atom.DrainAsync();

        // Should have 5 signals
        Assert.Equal(5, sink.Count);

        // Cleanup signals older than 2 minutes
        var removed = atom.Cleanup(TimeSpan.FromMinutes(2));

        // Should remove signals from operations 2, 3, 4 (3 signals)
        Assert.True(removed >= 3, $"Expected at least 3 signals removed, got {removed}");

        // Recent signals should remain
        var remaining = sink.Sense();
        Assert.True(remaining.Count <= 2, $"Expected <= 2 signals remaining, got {remaining.Count}");
    }

    [Fact]
    public async Task Atom_Cleanup_Int_RemovesOldestN()
    {
        var sink = new SignalSink();
        await using var atom = new TestAtom(sink);

        // Create operations with signals
        for (int i = 0; i < 10; i++)
        {
            var opId = await atom.EnqueueAsync(i);
            sink.Raise(new SignalEvent($"test.{i}", opId, null, DateTimeOffset.UtcNow.AddMilliseconds(i)));
        }

        await atom.DrainAsync();

        // Should have 10 signals
        Assert.Equal(10, sink.Count);

        // Cleanup oldest 5 signals
        var removed = atom.Cleanup(5);

        Assert.True(removed >= 5, $"Expected at least 5 signals removed, got {removed}");

        // Should have at most 5 signals remaining
        Assert.True(sink.Count <= 5, $"Expected <= 5 signals remaining, got {sink.Count}");
    }

    [Fact]
    public async Task Atom_Cleanup_Pattern_RemovesMatchingSignals()
    {
        var sink = new SignalSink();
        await using var atom = new TestAtom(sink);

        // Create operations with different signal patterns
        for (int i = 0; i < 5; i++)
        {
            var opId = await atom.EnqueueAsync(i);
            sink.Raise(new SignalEvent($"error.{i}", opId, null, DateTimeOffset.UtcNow));
            sink.Raise(new SignalEvent($"info.{i}", opId, null, DateTimeOffset.UtcNow));
        }

        await atom.DrainAsync();

        // Should have 10 signals (5 error + 5 info)
        Assert.Equal(10, sink.Count);

        // Cleanup only error signals
        var removed = atom.Cleanup("error.*");

        Assert.Equal(5, removed);

        // Should have 5 info signals remaining
        var remaining = sink.Sense();
        Assert.Equal(5, remaining.Count);
        Assert.All(remaining, s => Assert.StartsWith("info.", s.Signal));
    }

    [Fact]
    public async Task Atom_Cleanup_WithNoSink_ReturnsZero()
    {
        // Atom without signal sink
        await using var atom = new TestAtom(null);

        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        // Should not throw and return 0
        Assert.Equal(0, atom.Cleanup(TimeSpan.FromMinutes(1)));
        Assert.Equal(0, atom.Cleanup(10));
        Assert.Equal(0, atom.Cleanup("test.*"));
    }

    [Fact]
    public async Task Atom_Cleanup_OnlyRemovesOwnSignals()
    {
        var sharedSink = new SignalSink();

        // Two atoms sharing a sink
        await using var atom1 = new TestAtom(sharedSink);
        await using var atom2 = new TestAtom(sharedSink);

        // Atom1 creates signals
        for (int i = 0; i < 5; i++)
        {
            var opId = await atom1.EnqueueAsync(i);
            sharedSink.Raise(new SignalEvent($"atom1.{i}", opId, null, DateTimeOffset.UtcNow));
        }

        // Atom2 creates signals
        for (int i = 0; i < 5; i++)
        {
            var opId = await atom2.EnqueueAsync(i);
            sharedSink.Raise(new SignalEvent($"atom2.{i}", opId, null, DateTimeOffset.UtcNow));
        }

        await atom1.DrainAsync();
        await atom2.DrainAsync();

        // Should have 10 signals total
        Assert.Equal(10, sharedSink.Count);

        // Cleanup atom1's signals only
        var removed = atom1.Cleanup("atom1.*");

        Assert.Equal(5, removed);

        // Atom2's signals should remain
        var remaining = sharedSink.Sense();
        Assert.Equal(5, remaining.Count);
        Assert.All(remaining, s => Assert.StartsWith("atom2.", s.Signal));
    }

    #endregion

    #region SignalSink Cleanup Tests

    [Fact]
    public void Sink_ClearOlderThan_RemovesOldSignals()
    {
        var sink = new SignalSink();

        // Add signals with different ages
        sink.Raise(new SignalEvent("old1", 1, null, DateTimeOffset.UtcNow.AddMinutes(-10)));
        sink.Raise(new SignalEvent("old2", 2, null, DateTimeOffset.UtcNow.AddMinutes(-5)));
        sink.Raise(new SignalEvent("recent1", 3, null, DateTimeOffset.UtcNow.AddMinutes(-1)));
        sink.Raise(new SignalEvent("recent2", 4, null, DateTimeOffset.UtcNow));

        Assert.Equal(4, sink.Count);

        // Clear signals older than 2 minutes
        var removed = sink.ClearOlderThan(TimeSpan.FromMinutes(2));

        Assert.Equal(2, removed); // old1 and old2

        // Recent signals should remain
        var remaining = sink.Sense();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, s => s.Signal == "recent1");
        Assert.Contains(remaining, s => s.Signal == "recent2");
    }

    [Fact]
    public void Sink_ClearOldest_RemovesOldestN()
    {
        var sink = new SignalSink();

        // Add signals in order
        for (int i = 0; i < 10; i++)
        {
            sink.Raise(new SignalEvent($"signal.{i}", i, null, DateTimeOffset.UtcNow.AddMilliseconds(i)));
        }

        Assert.Equal(10, sink.Count);

        // Clear oldest 3
        var removed = sink.ClearOldest(3);

        Assert.Equal(3, removed);
        Assert.Equal(7, sink.Count);

        // Newest signals should remain
        var remaining = sink.Sense().OrderBy(s => s.Timestamp).ToList();
        Assert.StartsWith("signal.3", remaining[0].Signal);
    }

    [Fact]
    public void Sink_ClearOldest_WithZeroCount_ReturnsZero()
    {
        var sink = new SignalSink();
        sink.Raise("test");

        var removed = sink.ClearOldest(0);

        Assert.Equal(0, removed);
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void Sink_ClearOldest_WithMoreThanAvailable_RemovesAll()
    {
        var sink = new SignalSink();

        sink.Raise("signal1");
        sink.Raise("signal2");

        Assert.Equal(2, sink.Count);

        // Request to clear more than available
        var removed = sink.ClearOldest(100);

        Assert.Equal(2, removed); // Only 2 available
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Sink_ClearOlderThan_WithZeroAge_ClearsAll()
    {
        var sink = new SignalSink();

        sink.Raise("signal1");
        sink.Raise("signal2");
        sink.Raise("signal3");

        Assert.Equal(3, sink.Count);

        // Clear everything older than "now" (which is everything)
        var removed = sink.ClearOlderThan(TimeSpan.Zero);

        Assert.Equal(3, removed);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Sink_ClearOlderThan_WithVeryLongAge_ClearsNothing()
    {
        var sink = new SignalSink();

        sink.Raise("signal1");
        sink.Raise("signal2");

        Assert.Equal(2, sink.Count);

        // Try to clear signals older than 1 year (nothing is that old for just-created signals)
        var removed = sink.ClearOlderThan(TimeSpan.FromDays(365));

        Assert.Equal(0, removed);
        Assert.Equal(2, sink.Count);
    }

    #endregion

    /// <summary>
    /// Test atom for cleanup testing.
    /// </summary>
    private class TestAtom : AtomBase<EphemeralWorkCoordinator<int>>
    {
        public TestAtom(SignalSink? sink) : base(
            new EphemeralWorkCoordinator<int>(
                async (item, ct) => await Task.Delay(10, ct),
                new EphemeralOptions
                {
                    MaxConcurrency = 1,
                    MaxTrackedOperations = 100,
                    Signals = sink
                }))
        {
        }

        public async Task<long> EnqueueAsync(int item)
        {
            return await Coordinator.EnqueueWithIdAsync(item);
        }

        protected override void Complete() => Coordinator.Complete();
        protected override Task DrainInternalAsync(CancellationToken ct) => Coordinator.DrainAsync(ct);
        public override IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot() => Coordinator.GetSnapshot();
    }
}
