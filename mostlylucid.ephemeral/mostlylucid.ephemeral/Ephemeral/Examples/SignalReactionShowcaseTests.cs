using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class SignalReactionShowcaseTests
{
    [Fact]
    public async Task Emits_And_Reacts_Via_Sink_And_Polling()
    {
        var result = await SignalReactionShowcase.RunAsync(itemCount: 12);

        Assert.Equal(12, result.DispatchedHits); // dispatcher saw completions
        Assert.Equal(12, result.PolledHits);    // Polling saw completions
        Assert.Contains(result.Signals, s => s.StartsWith("stage.start"));
        Assert.Contains(result.Signals, s => s.StartsWith("stage.done"));
    }
}
