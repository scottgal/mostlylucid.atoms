using System.Threading.Tasks;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Patterns.PersistentWindow;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class PersistentSignalWindowTests : IAsyncLifetime
{
    private readonly string _dbPath;

    public PersistentSignalWindowTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_signals_{Guid.NewGuid():N}.db");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        // Cleanup database file
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
            if (File.Exists($"{_dbPath}-wal"))
                File.Delete($"{_dbPath}-wal");
            if (File.Exists($"{_dbPath}-shm"))
                File.Delete($"{_dbPath}-shm");
        }
        catch
        {
            // Ignore cleanup errors
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Window_RaisesAndSensesSignals()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromSeconds(30));

        window.Raise("order.created", key: "order-1");
        window.Raise("order.shipped", key: "order-1");
        window.Raise("payment.completed", key: "pay-1");

        var orderSignals = window.Sense("order.*");
        Assert.Equal(2, orderSignals.Count);

        // Filter out internal window.* signals
        var userSignals = window.Sense(s => !s.Signal.StartsWith("window."));
        Assert.Equal(3, userSignals.Count);
    }

    [Fact]
    public async Task Window_FlushesToDisk()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromMilliseconds(100));

        window.Raise("test.signal.1");
        window.Raise("test.signal.2");
        window.Raise("test.signal.3");

        // Wait for flush
        await Task.Delay(300);

        var stats = window.GetStats();
        // LastFlushedId is a hash-based ID, can be negative, just verify it's been set
        Assert.NotEqual(0, stats.LastFlushedId);
    }

    [Fact]
    public async Task Window_LoadsFromDisk()
    {
        // First window - write signals and ensure they're flushed
        await using (var window1 = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromMilliseconds(100)))
        {
            window1.Raise("persistent.signal.1");
            window1.Raise("persistent.signal.2");

            // Verify signals exist in memory
            var beforeFlush = window1.Sense("persistent.*");
            Assert.Equal(2, beforeFlush.Count);

            // Flush to disk
            await window1.FlushAsync();
        }

        // Second window - load signals (no maxAge to load all)
        await using var window2 = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromSeconds(30));
        await window2.LoadFromDiskAsync();

        var signals = window2.Sense("persistent.*");
        Assert.Equal(2, signals.Count);
    }

    [Fact]
    public async Task Window_RespectsMaxAgeOnLoad()
    {
        // First window - write signals with a delay
        await using (var window1 = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromMilliseconds(100)))
        {
            window1.Raise("old.signal");
            await Task.Delay(200); // Wait for flush
        }

        // Wait a bit so signal ages
        await Task.Delay(100);

        // Second window - load with very short max age
        await using var window2 = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromSeconds(30));
        await window2.LoadFromDiskAsync(maxAge: TimeSpan.FromMilliseconds(1));

        // Signal should be too old
        var signals = window2.Sense("old.*");
        Assert.Empty(signals);
    }

    [Fact]
    public async Task Window_GetStats_ReturnsCorrectCounts()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromMilliseconds(100));

        window.Raise("stat.test.1");
        window.Raise("stat.test.2");

        // User signals plus internal window.initialized and window.raise signals
        var userSignalCount = window.Sense(s => s.Signal.StartsWith("stat.")).Count;
        Assert.Equal(2, userSignalCount);

        // Wait for flush
        await Task.Delay(200);

        var stats = window.GetStats();
        // LastFlushedId is a hash-based ID, can be negative, just verify it's been set
        Assert.NotEqual(0, stats.LastFlushedId);
    }

    [Fact]
    public async Task Window_HandlesHighVolume()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromMilliseconds(200));

        // Raise many signals
        for (var i = 0; i < 100; i++)
        {
            window.Raise($"volume.test.{i % 10}", key: $"key-{i}");
        }

        // Wait for flush
        await Task.Delay(500);

        var signals = window.Sense("volume.*");
        Assert.Equal(100, signals.Count);
    }

    [Fact]
    public async Task Window_RaiseSignalEvent_PreservesProperties()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromSeconds(30));

        var evt = new SignalEvent("custom.event", 12345, "custom-key", DateTimeOffset.UtcNow);
        window.Raise(evt);

        var signals = window.Sense("custom.*");
        Assert.Single(signals);
        Assert.Equal("custom.event", signals[0].Signal);
        Assert.Equal("custom-key", signals[0].Key);
    }

    [Fact]
    public async Task Window_HasSignal_ViaSignalSink()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromSeconds(30));

        window.Raise("detect.me");
        window.Raise("other.signal");

        Assert.True(window.Sink.Detect("detect.me"));
        Assert.True(window.Sink.Detect(s => s.Signal.StartsWith("other")));
        Assert.False(window.Sink.Detect("nonexistent"));
    }

    [Fact]
    public async Task Window_CountSignals_ViaSense()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromSeconds(30));

        window.Raise("count.a");
        window.Raise("count.b");
        window.Raise("count.c");
        window.Raise("other.d");

        Assert.Equal(3, window.Sense("count.*").Count);
        Assert.Single(window.Sense("other.*"));
        // Filter out internal window.* signals when counting user signals
        Assert.Equal(4, window.Sense(s => !s.Signal.StartsWith("window.")).Count);
    }

    [Fact]
    public async Task Window_EmitsFlushSignals()
    {
        var diagnosticSignals = new List<SignalEvent>();
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromMilliseconds(100));
        window.Sink.SignalRaised += e => diagnosticSignals.Add(e);

        window.Raise("trigger.flush");

        // Wait for flush
        await Task.Delay(300);

        Assert.Contains(diagnosticSignals, s => s.Signal.Contains("window.flush"));
    }

    [Fact]
    public async Task Window_ManualFlush()
    {
        await using var window = new PersistentSignalWindow($"Data Source={_dbPath}", TimeSpan.FromHours(1)); // Long interval

        window.Raise("manual.flush.test");

        var statsBefore = window.GetStats();
        var beforeFlushId = statsBefore.LastFlushedId;
        Assert.Equal(0, beforeFlushId); // Not yet flushed

        await window.FlushAsync();

        var statsAfter = window.GetStats();
        // After flush, the LastFlushedId should have been set (to a non-zero hash value)
        Assert.NotEqual(0, statsAfter.LastFlushedId);
    }
}
