using Mostlylucid.Helpers.Ephemeral;
using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class SignalAnomalyDetectorTests
{
    [Fact]
    public void Detects_When_Threshold_Reached_In_Window()
    {
        var sink = new SignalSink(maxCapacity: 10, maxAge: TimeSpan.FromSeconds(10));
        var detector = new SignalAnomalyDetector(sink, pattern: "error.*", threshold: 2, window: TimeSpan.FromSeconds(5));

        sink.Raise(new SignalEvent("error.timeout", 1, null, DateTimeOffset.UtcNow));
        sink.Raise(new SignalEvent("error.io", 2, null, DateTimeOffset.UtcNow));

        Assert.True(detector.IsAnomalous());
    }

    [Fact]
    public void Ignores_Outside_Window()
    {
        var sink = new SignalSink(maxCapacity: 10, maxAge: TimeSpan.FromSeconds(10));
        var detector = new SignalAnomalyDetector(sink, pattern: "error.*", threshold: 1, window: TimeSpan.FromMilliseconds(100));

        sink.Raise(new SignalEvent("error.old", 1, null, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5)));

        Assert.False(detector.IsAnomalous());
    }
}
