using Mostlylucid.Ephemeral.Atoms.SignalAware;
using Xunit;

namespace Mostlylucid.Ephemeral.Atoms.SignalAware.Tests;

public class SignalAwareAtomTests
{
    [Fact]
    public async Task ManuallyRaisedAmbientSignal_BlocksEnqueue()
    {
        await using var atom = new SignalAwareAtom<int>(
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            cancelOn: new HashSet<string> { "circuit-open" });

        atom.Raise("circuit-open");

        var id = await atom.EnqueueAsync(1);
        Assert.Equal(-1, id);
    }

    [Fact]
    public async Task RealSignalOnSharedSink_BlocksEnqueue()
    {
        // This is the fix under test: previously EnqueueAsync only checked a local,
        // manually-populated ambient set, never the live SignalSink — so a signal raised
        // through the actual shared sink (the normal way signals happen in practice) had no
        // effect on intake here, only on already-enqueued items via the coordinator's own
        // CancelOnSignals handling.
        var sink = new SignalSink();
        await using var atom = new SignalAwareAtom<int>(
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            cancelOn: new HashSet<string> { "circuit-open" },
            signals: sink);

        sink.Raise("circuit-open");

        var id = await atom.EnqueueAsync(1);
        Assert.Equal(-1, id);
    }

    [Fact]
    public async Task NoCancelSignal_EnqueueSucceeds()
    {
        var sink = new SignalSink();
        await using var atom = new SignalAwareAtom<int>(
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            cancelOn: new HashSet<string> { "circuit-open" },
            signals: sink);

        var id = await atom.EnqueueAsync(1);
        Assert.True(id > 0);

        await atom.DrainAsync();
        Assert.Equal(1, atom.Stats().Completed);
    }

    [Fact]
    public async Task UnrelatedSignalOnSink_DoesNotBlockEnqueue()
    {
        var sink = new SignalSink();
        await using var atom = new SignalAwareAtom<int>(
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            cancelOn: new HashSet<string> { "circuit-open" },
            signals: sink);

        sink.Raise("something.else");

        var id = await atom.EnqueueAsync(1);
        Assert.True(id > 0);
    }
}
