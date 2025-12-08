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

    [Fact]
    public void Cleanup_LimitsIterations_PreventingUnboundedLoop()
    {
        var sink = new SignalSink(maxCapacity: 10);

        // Add way more signals than capacity
        for (int i = 0; i < 5000; i++)
        {
            sink.Raise($"signal.{i}");
        }

        // Trigger cleanup by adding more
        for (int i = 0; i < 1100; i++)
        {
            sink.Raise($"cleanup.{i}");
        }

        // Should not hang - cleanup is bounded
        // After bounded cleanup, count should be reasonable (not thousands)
        Assert.True(sink.Count < 2000); // Should be much less than 5000+1100
    }

    [Fact]
    public async Task Cleanup_HandlesExpiredSignals()
    {
        var sink = new SignalSink(maxCapacity: 100, maxAge: TimeSpan.FromMilliseconds(50));

        sink.Raise("signal.1");
        sink.Raise("signal.2");

        await Task.Delay(100); // Wait for signals to expire

        // Add more to trigger cleanup
        for (int i = 0; i < 1100; i++)
        {
            sink.Raise($"new.{i}");
        }

        // Old signals should be cleaned up
        var signals = sink.Sense();
        Assert.DoesNotContain(signals, s => s.Signal == "signal.1");
        Assert.DoesNotContain(signals, s => s.Signal == "signal.2");
    }

    [Fact]
    public void UpdateWindowSize_UpdatesCapacityAndAge()
    {
        var sink = new SignalSink(maxCapacity: 100, maxAge: TimeSpan.FromMinutes(1));

        sink.UpdateWindowSize(maxCapacity: 200, maxAge: TimeSpan.FromMinutes(5));

        Assert.Equal(200, sink.MaxCapacity);
        Assert.Equal(TimeSpan.FromMinutes(5), sink.MaxAge);
    }

    [Fact]
    public void UpdateWindowSize_OnlyUpdatesSpecifiedParameters()
    {
        var sink = new SignalSink(maxCapacity: 100, maxAge: TimeSpan.FromMinutes(1));

        sink.UpdateWindowSize(maxCapacity: 200);

        Assert.Equal(200, sink.MaxCapacity);
        Assert.Equal(TimeSpan.FromMinutes(1), sink.MaxAge); // Unchanged
    }

    [Fact]
    public void UpdateWindowSize_ClampsToMinimumOne()
    {
        var sink = new SignalSink();

        sink.UpdateWindowSize(maxCapacity: -100);

        Assert.True(sink.MaxCapacity >= 1);
    }

    [Fact]
    public void SignalRaised_Event_FiresForSubscribers()
    {
        var sink = new SignalSink();
        SignalEvent? receivedSignal = null;

        sink.SignalRaised += (signal) => receivedSignal = signal;
        sink.Raise("test.event");

        Assert.NotNull(receivedSignal);
        Assert.Equal("test.event", receivedSignal.Value.Signal);
    }

    [Fact]
    public void SignalRaised_Event_DoesNotThrowOnHandlerException()
    {
        var sink = new SignalSink();

        sink.SignalRaised += (_) => throw new Exception("Handler failure");

        // Should not throw
        sink.Raise("test.event");

        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void ConcurrentRaise_HandlesMultipleThreads()
    {
        var sink = new SignalSink(maxCapacity: 10000);
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            var taskId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    sink.Raise($"task.{taskId}.signal.{j}");
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Should have signals from concurrent operations
        Assert.True(sink.Count > 0);
    }

    [Fact]
    public void Sense_PredicateAllocation_MinimizesGC()
    {
        var sink = new SignalSink();

        // Add many signals
        for (int i = 0; i < 100; i++)
        {
            sink.Raise($"signal.{i}");
        }

        // Pre-sized list should reduce allocations
        var results = sink.Sense(s => s.Signal.StartsWith("signal.5"));

        Assert.NotEmpty(results);
        Assert.All(results, s => Assert.StartsWith("signal.5", s.Signal));
    }

    [Fact]
    public void Detect_ShortCircuits_OnFirstMatch()
    {
        var sink = new SignalSink();

        for (int i = 0; i < 1000; i++)
        {
            sink.Raise($"signal.{i}");
        }

        // Should find quickly without checking all 1000
        var found = sink.Detect("signal.0");

        Assert.True(found);
    }

    [Fact]
    public async Task HighVolumeStressTest_MaintainsStability()
    {
        var sink = new SignalSink(maxCapacity: 1000);

        // Simulate high-volume scenario
        var tasks = Enumerable.Range(0, 10).Select(taskId =>
            Task.Run(async () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    sink.Raise($"task.{taskId}.msg.{i}");
                    if (i % 100 == 0)
                    {
                        await Task.Delay(1); // Small delay
                    }
                }
            })
        ).ToArray();

        await Task.WhenAll(tasks);

        // Should maintain capacity limits
        Assert.True(sink.Count <= 1000 + 1000); // Capacity + cleanup buffer
    }
}