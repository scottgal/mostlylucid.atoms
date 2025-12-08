using Xunit;
using Mostlylucid.Ephemeral.Atoms.WindowSize;

namespace Mostlylucid.Ephemeral.Tests;

public class WindowSizeAtomTests
{
    [Fact]
    public async Task SetCapacity_UpdatesSignalSinkCapacity()
    {
        var sink = new SignalSink(maxCapacity: 100);
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.size.set:200");
        await Task.Delay(50); // Give event handler time to process

        Assert.Equal(200, sink.MaxCapacity);
    }

    [Fact]
    public async Task IncreaseCapacity_AddsToCurrentCapacity()
    {
        var sink = new SignalSink(maxCapacity: 100);
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.size.increase:50");
        await Task.Delay(50);

        Assert.Equal(150, sink.MaxCapacity);
    }

    [Fact]
    public async Task DecreaseCapacity_SubtractsFromCurrentCapacity()
    {
        var sink = new SignalSink(maxCapacity: 200);
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.size.decrease:50");
        await Task.Delay(50);

        Assert.Equal(150, sink.MaxCapacity);
    }

    [Fact]
    public async Task SetCapacity_ClampsToMinimum()
    {
        var sink = new SignalSink(maxCapacity: 100);
        var options = new WindowSizeAtomOptions { MinCapacity = 50, MaxCapacity = 1000 };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("window.size.set:10"); // Below minimum
        await Task.Delay(50);

        Assert.Equal(50, sink.MaxCapacity); // Clamped to minimum
    }

    [Fact]
    public async Task SetCapacity_ClampsToMaximum()
    {
        var sink = new SignalSink(maxCapacity: 100);
        var options = new WindowSizeAtomOptions { MinCapacity = 50, MaxCapacity = 1000 };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("window.size.set:5000"); // Above maximum
        await Task.Delay(50);

        Assert.Equal(1000, sink.MaxCapacity); // Clamped to maximum
    }

    [Fact]
    public async Task SetRetention_UpdatesSignalSinkMaxAge()
    {
        var sink = new SignalSink();
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.time.set:00:05:00"); // 5 minutes
        await Task.Delay(50);

        Assert.Equal(TimeSpan.FromMinutes(5), sink.MaxAge);
    }

    [Fact]
    public async Task SetRetention_ParsesSecondsFormat()
    {
        var sink = new SignalSink();
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.time.set:30s");
        await Task.Delay(50);

        Assert.Equal(TimeSpan.FromSeconds(30), sink.MaxAge);
    }

    [Fact]
    public async Task SetRetention_ParsesMillisecondsFormat()
    {
        var sink = new SignalSink();
        var options = new WindowSizeAtomOptions
        {
            MinRetention = TimeSpan.FromMilliseconds(1) // Allow small values
        };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("window.time.set:500ms");
        await Task.Delay(100);

        // The parser interprets "500ms" -> 500 milliseconds
        Assert.Equal(TimeSpan.FromMilliseconds(500), sink.MaxAge);
    }

    [Fact]
    public async Task IncreaseRetention_AddsToCurrentTime()
    {
        var sink = new SignalSink(maxAge: TimeSpan.FromMinutes(1));
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.time.increase:30s");
        await Task.Delay(50);

        Assert.Equal(TimeSpan.FromSeconds(90), sink.MaxAge);
    }

    [Fact]
    public async Task DecreaseRetention_SubtractsFromCurrentTime()
    {
        var sink = new SignalSink(maxAge: TimeSpan.FromMinutes(2));
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.time.decrease:30s");
        await Task.Delay(50);

        Assert.Equal(TimeSpan.FromSeconds(90), sink.MaxAge);
    }

    [Fact]
    public async Task SetRetention_ClampsToMinimum()
    {
        var sink = new SignalSink();
        var options = new WindowSizeAtomOptions
        {
            MinRetention = TimeSpan.FromSeconds(10),
            MaxRetention = TimeSpan.FromHours(1)
        };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("window.time.set:1s"); // Below minimum
        await Task.Delay(50);

        Assert.Equal(TimeSpan.FromSeconds(10), sink.MaxAge);
    }

    [Fact]
    public async Task SetRetention_ClampsToMaximum()
    {
        var sink = new SignalSink();
        var options = new WindowSizeAtomOptions
        {
            MinRetention = TimeSpan.FromSeconds(10),
            MaxRetention = TimeSpan.FromHours(1)
        };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("window.time.set:10:00:00"); // 10 hours, above maximum
        await Task.Delay(50);

        Assert.Equal(TimeSpan.FromHours(1), sink.MaxAge);
    }

    [Fact]
    public async Task InvalidSignal_DoesNotCrash()
    {
        var sink = new SignalSink(maxCapacity: 100);
        await using var atom = new WindowSizeAtom(sink);

        sink.Raise("window.size.set:invalid");
        sink.Raise("window.time.set:invalid");
        sink.Raise("unrelated.signal");
        await Task.Delay(50);

        // Should still have original capacity
        Assert.Equal(100, sink.MaxCapacity);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromSignals()
    {
        var sink = new SignalSink(maxCapacity: 100);
        var atom = new WindowSizeAtom(sink);

        await atom.DisposeAsync();

        // Raise signal after disposal
        sink.Raise("window.size.set:200");
        await Task.Delay(50);

        // Should not have updated
        Assert.Equal(100, sink.MaxCapacity);
    }

    [Fact]
    public async Task CustomCommands_WorkCorrectly()
    {
        var sink = new SignalSink(maxCapacity: 100);
        var options = new WindowSizeAtomOptions
        {
            CapacitySetCommand = "custom.size",
            TimeSetCommand = "custom.time"
        };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("custom.size:500");
        sink.Raise("custom.time:10s");
        await Task.Delay(50);

        Assert.Equal(500, sink.MaxCapacity);
        Assert.Equal(TimeSpan.FromSeconds(10), sink.MaxAge);
    }

    [Fact]
    public async Task ConcurrentSignals_HandleSafely()
    {
        var sink = new SignalSink();
        await using var atom = new WindowSizeAtom(sink);

        // Fire multiple signals concurrently
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => sink.Raise($"window.size.set:{100 + i}"))
        ).ToArray();

        await Task.WhenAll(tasks);
        await Task.Delay(100);

        // Should have some valid capacity (one of the 100 values)
        Assert.InRange(sink.MaxCapacity, 100, 199);
    }

    [Fact]
    public async Task NegativeValues_ClampedToMinimum()
    {
        var sink = new SignalSink(maxCapacity: 100);
        var options = new WindowSizeAtomOptions { MinCapacity = 10 };
        await using var atom = new WindowSizeAtom(sink, options);

        sink.Raise("window.size.set:-50");
        await Task.Delay(50);

        Assert.Equal(10, sink.MaxCapacity);
    }

    [Fact]
    public async Task NestedSignalName_ParsesCorrectly()
    {
        var sink = new SignalSink(maxCapacity: 100);
        await using var atom = new WindowSizeAtom(sink);

        // Signal with prefix should still work
        sink.Raise("app.window.size.set:300");
        await Task.Delay(50);

        Assert.Equal(300, sink.MaxCapacity);
    }

    [Fact]
    public void Constructor_NullSink_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowSizeAtom(null!));
    }

    [Fact]
    public async Task Constructor_NullOptions_UsesDefaults()
    {
        var sink = new SignalSink();
        await using var atom = new WindowSizeAtom(sink, null);

        // Should use default commands
        sink.Raise("window.size.set:500");
        await Task.Delay(50);

        Assert.Equal(500, sink.MaxCapacity);
    }
}
