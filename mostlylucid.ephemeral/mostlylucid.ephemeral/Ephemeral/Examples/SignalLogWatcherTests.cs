using Mostlylucid.Helpers.Ephemeral;
using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class SignalLogWatcherTests
{
    [Fact]
    public async Task Invokes_OnMatch_ForPattern()
    {
        var sink = new SignalSink(maxCapacity: 16, maxAge: TimeSpan.FromSeconds(10));
        var hits = new List<SignalEvent>();

        await using var watcher = new SignalLogWatcher(
            sink,
            evt => hits.Add(evt),
            pattern: "error.*",
            pollInterval: TimeSpan.FromMilliseconds(20));

        sink.Raise(new SignalEvent("info.ready", 1, null, DateTimeOffset.UtcNow));
        sink.Raise(new SignalEvent("error.timeout", 2, null, DateTimeOffset.UtcNow));

        await Task.Delay(100);

        Assert.Single(hits);
        Assert.Equal("error.timeout", hits[0].Signal);
    }
}
