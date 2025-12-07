using Mostlylucid.Helpers.Ephemeral;
using Mostlylucid.Helpers.Ephemeral.Atoms;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Atoms;

public class SignalAwareAtomTests
{
    [Fact]
    public async Task CancelOnSignals_Skips_New_Items()
    {
        var processed = 0;
        var cancel = new HashSet<string> { "circuit-open" };

        await using var atom = new SignalAwareAtom<int>(
            async (item, ct) =>
            {
                await Task.Delay(1, ct);
                processed++;
            },
            cancelOn: cancel,
            maxConcurrency: 1); // ensure signaling item runs first

        atom.Raise("circuit-open"); // seed ambient cancel signal
        for (var i = 0; i < 5; i++) await atom.EnqueueAsync(i);

        await atom.DrainAsync();
        Assert.Equal(0, processed);
        Assert.Equal(0, atom.Stats().Completed);
    }
}
