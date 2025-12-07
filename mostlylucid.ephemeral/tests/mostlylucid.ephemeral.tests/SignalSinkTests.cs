using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class SignalSinkTests
{
    [Fact]
    public void Raise_AddsSignalToSink()
    {
        var sink = new SignalSink();
        sink.Raise("test.signal");

        var snapshot = sink.Sense();
        Assert.Single(snapshot);
        Assert.Equal("test.signal", snapshot[0].Signal);
    }

    [Fact]
    public void Sense_ReturnsAllSignals()
    {
        var sink = new SignalSink();
        sink.Raise("signal.1");
        sink.Raise("signal.2");
        sink.Raise("signal.3");

        var snapshot = sink.Sense();
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void Sense_WithPredicate_FiltersSignals()
    {
        var sink = new SignalSink();
        sink.Raise("error.timeout");
        sink.Raise("error.connection");
        sink.Raise("warning.low");

        var errorSignals = sink.Sense(s => s.Signal.StartsWith("error."));
        Assert.Equal(2, errorSignals.Count);
    }

    [Fact]
    public void Detect_ReturnsTrueForExistingSignal()
    {
        var sink = new SignalSink();
        sink.Raise("test.signal");

        Assert.True(sink.Detect("test.signal"));
        Assert.False(sink.Detect("other.signal"));
    }

    [Fact]
    public void Detect_WithPredicate_MatchesSignal()
    {
        var sink = new SignalSink();
        sink.Raise("error.timeout");

        Assert.True(sink.Detect(s => s.Signal.StartsWith("error.")));
        Assert.False(sink.Detect(s => s.Signal.StartsWith("warning.")));
    }

    [Fact]
    public void Count_ReturnsNumberOfSignals()
    {
        var sink = new SignalSink();
        sink.Raise("signal.1");
        sink.Raise("signal.2");

        Assert.Equal(2, sink.Count);
    }
}
