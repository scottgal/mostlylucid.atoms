using Mostlylucid.Helpers.Ephemeral;
using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class DynamicConcurrencyDemoTests
{
    [Fact]
    public async Task Scales_Up_And_Down_On_Signals()
    {
        var sink = new SignalSink(maxCapacity: 16, maxAge: TimeSpan.FromSeconds(5));
        await using var demo = new DynamicConcurrencyDemo<int>(
            async (item, ct) => await Task.Delay(5, ct),
            sink,
            minConcurrency: 1,
            maxConcurrency: 4,
            scaleUpPattern: "load.high",
            scaleDownPattern: "load.low");

        var initial = demo.EnqueueAsync(1); // force creation
        await initial;

        sink.Raise(new SignalEvent("load.high", 1, null, DateTimeOffset.UtcNow));
        await SpinUntilAsync(() => demo.CurrentMaxConcurrency > 1, TimeSpan.FromSeconds(2));
        sink.Raise(new SignalEvent("load.low", 2, null, DateTimeOffset.UtcNow));
        await SpinUntilAsync(() => demo.CurrentMaxConcurrency <= 2, TimeSpan.FromSeconds(2));

        await demo.DrainAsync();

        static async Task SpinUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var start = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - start < timeout)
            {
                if (condition()) return;
                await Task.Delay(50);
            }
            Assert.True(condition());
        }
    }
}
