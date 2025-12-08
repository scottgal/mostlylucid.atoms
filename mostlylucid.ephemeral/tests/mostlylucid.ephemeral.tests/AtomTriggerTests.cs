using Mostlylucid.Ephemeral.Atoms.Molecules;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public sealed class AtomTriggerTests
{
    [Fact]
    public async Task ActionRunsWhenSignalMatches()
    {
        var sink = new SignalSink();
        var triggered = 0;

        using var trigger = new AtomTrigger(sink, "order.*", async (signal, ct) =>
        {
            Interlocked.Increment(ref triggered);
            await Task.Delay(1, ct).ConfigureAwait(false);
        });

        sink.Raise("order.ready", "order-2");
        await Task.Delay(50);

        Assert.Equal(1, triggered);
    }
}