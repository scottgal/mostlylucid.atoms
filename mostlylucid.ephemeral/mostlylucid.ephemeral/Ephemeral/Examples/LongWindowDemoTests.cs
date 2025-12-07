using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class LongWindowDemoTests
{
    [Fact]
    public async Task ShortWindow_StaysSmall()
    {
        const int window = 10;
        var result = await LongWindowDemo.RunAsync(totalItems: 200, windowSize: window);

        Assert.Equal(window, result.WindowSize);
        Assert.Equal(200, result.TotalItems);
        Assert.InRange(result.TrackedCount, 1, window);
    }

    [Fact]
    public async Task LongWindow_CapturesAllWhenBelowLimit()
    {
        const int window = 1200;
        const int total = 1000;

        var result = await LongWindowDemo.RunAsync(totalItems: total, windowSize: window, workDelayMs: 1);

        Assert.Equal(window, result.WindowSize);
        Assert.Equal(total, result.TotalItems);
        Assert.Equal(total, result.TrackedCount);
    }
}
