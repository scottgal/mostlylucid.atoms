using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class AdvancedCoordinationTests
{
    #region Staged Pipeline

    [Fact]
    public async Task StagedPipeline_ExecutesInOrder()
    {
        var sink = new SignalSink();
        var stages = new List<int>();

        var builder = new StagedPipelineBuilder<int>(sink)
            .AddStage(0, async (n, ct) =>
            {
                lock (stages)
                {
                    stages.Add(0);
                }

                await Task.Delay(10, ct);
            }, "init")
            .AddStage(1, async (n, ct) =>
            {
                lock (stages)
                {
                    stages.Add(1);
                }

                await Task.Delay(10, ct);
            }, "process");

        await builder.ExecuteAsync(new[] { 1, 2, 3 });

        Assert.Equal(2, stages.Distinct().Count());
        Assert.True(sink.Detect("stage.start:init"));
        Assert.True(sink.Detect("stage.complete:init"));
        Assert.True(sink.Detect("stage.start:process"));
        Assert.True(sink.Detect("stage.complete:process"));
    }

    #endregion

    #region Typed Signal Keys

    [Fact]
    public void SignalKey_ImplicitConversionToString()
    {
        var key = new SignalKey<double>("bot.score");
        string name = key;
        Assert.Equal("bot.score", name);
    }

    [Fact]
    public void TypedSignalSink_RaiseWithKey()
    {
        var key = new SignalKey<double>("bot.score");
        var sink = new TypedSignalSink<double>();

        sink.Raise(key, 0.95, "test-op");

        var signals = sink.Sense(key);
        Assert.Single(signals);
        Assert.Equal(0.95, signals[0].Payload);
    }

    #endregion

    #region Signal Aggregation Window

    [Fact]
    public void SignalAggregationWindow_QueriesWithinWindow()
    {
        var sink = new TypedSignalSink<double>();
        sink.Raise("score.ua", 0.5);
        sink.Raise("score.ip", 0.3);
        sink.Raise("score.header", 0.7);

        var window = new SignalAggregationWindow<double>(sink, TimeSpan.FromMinutes(5));

        Assert.Equal(3, window.Count("score.*"));
        Assert.Equal(1.5, window.Sum(p => p, "score.*"), 2);
        Assert.Equal(0.5, window.Average(p => p, "score.*"), 2);
    }

    [Fact]
    public void SignalAggregationWindow_GroupBy()
    {
        var sink = new TypedSignalSink<double>();
        sink.Raise("score.ua", 0.5, "detector-ua");
        sink.Raise("score.ua", 0.6, "detector-ua");
        sink.Raise("score.ip", 0.3, "detector-ip");

        var window = new SignalAggregationWindow<double>(sink);

        var groups = window.GroupBy(e => e.Signal, "score.*").ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.First(g => g.Key == "score.ua").Count());
    }

    #endregion

    #region Contributor Tracking

    [Fact]
    public async Task ContributorTracker_TracksCompletion()
    {
        var tracker = new ContributorTracker<double>(new[] { "UA", "IP", "Header" });

        tracker.Complete("UA", 0.5);
        tracker.Complete("IP", 0.3);

        Assert.Equal(2, tracker.CompletedCount);
        Assert.Equal(3, tracker.ExpectedCount);

        tracker.Complete("Header", 0.7);

        Assert.Equal(3, tracker.CompletedCount);

        var results = tracker.GetCompletedResults();
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task ContributorTracker_QuorumReached()
    {
        var tracker = new ContributorTracker<double>(new[] { "UA", "IP", "Header", "Behavioral", "ClientSide" });

        var waitTask = tracker.WaitForQuorumAsync(3, TimeSpan.FromSeconds(2));

        tracker.Complete("UA", 0.5);
        tracker.Complete("IP", 0.3);
        tracker.Complete("Header", 0.7);

        var result = await waitTask;
        Assert.True(result.Reached);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(2, result.PendingCount);
    }

    [Fact]
    public async Task ContributorTracker_QuorumTimeout()
    {
        var tracker = new ContributorTracker<double>(new[] { "UA", "IP", "Header" });

        tracker.Complete("UA", 0.5);
        // Only 1 of 3, need 3

        var result = await tracker.WaitForQuorumAsync(3, TimeSpan.FromMilliseconds(100));

        Assert.False(result.Reached);
        Assert.True(result.TimedOut);
        Assert.Equal(1, result.CompletedCount);
    }

    [Fact]
    public void ContributorTracker_TryGetResult()
    {
        var tracker = new ContributorTracker<double>(new[] { "UA", "IP" });

        tracker.Complete("UA", 0.5);

        Assert.True(tracker.TryGetResult("UA", out var score));
        Assert.Equal(0.5, score);

        Assert.False(tracker.TryGetResult("IP", out _));
    }

    #endregion

    #region Operation Dependencies

    [Fact]
    public async Task DependencyCoordinator_ExecutesInOrder()
    {
        var order = new List<string>();
        var coordinator = new DependencyCoordinator<string>(
            async (name, ct) =>
            {
                await Task.Delay(10, ct);
                lock (order)
                {
                    order.Add(name);
                }
            },
            4);

        coordinator.AddOperation("A", "A");
        coordinator.AddOperation("B", "B", "A");
        coordinator.AddOperation("C", "C", "A");
        coordinator.AddOperation("D", "D", "B", "C");

        await coordinator.ExecuteAsync();
        await coordinator.DisposeAsync();

        Assert.Equal("A", order[0]); // A first
        Assert.Equal("D", order[3]); // D last
        // B and C can be in either order after A
    }

    [Fact]
    public async Task DependencyCoordinator_DetectsCycle()
    {
        var coordinator = new DependencyCoordinator<string>(
            async (name, ct) => await Task.CompletedTask,
            2);

        coordinator.AddOperation("A", "A", "C");
        coordinator.AddOperation("B", "B", "A");
        coordinator.AddOperation("C", "C", "B");

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync());
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DependencyCoordinator_EmitsSignals()
    {
        var sink = new SignalSink();
        var coordinator = new DependencyCoordinator<string>(
            async (name, ct) => await Task.Delay(10, ct),
            2,
            sink);

        coordinator.AddOperation("test", "test");
        await coordinator.ExecuteAsync();

        Assert.True(sink.Detect(s => s.Signal == "operation.start:test"));
        Assert.True(sink.Detect(s => s.Signal == "operation.done:test"));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DependencyCoordinator_SkipsOnDependencyFailure()
    {
        var executed = new List<string>();
        var coordinator = new DependencyCoordinator<string>(
            async (name, ct) =>
            {
                if (name == "B")
                    throw new Exception("B failed");
                lock (executed)
                {
                    executed.Add(name);
                }
            },
            2);

        coordinator.AddOperation("A", "A");
        coordinator.AddOperation("B", "B", "A");
        coordinator.AddOperation("C", "C", "B"); // Should be skipped

        await Assert.ThrowsAsync<Exception>(() => coordinator.ExecuteAsync());
        await coordinator.DisposeAsync();

        Assert.Contains("A", executed);
        Assert.DoesNotContain("C", executed);
    }

    #endregion

    #region Early Exit Coordinator

    [Fact]
    public async Task EarlyExitCoordinator_ExitsOnSignal()
    {
        var sink = new SignalSink();
        var processed = 0;

        await using var coordinator = new EarlyExitResultCoordinator<int, int>(
            async (n, ct) =>
            {
                await Task.Delay(50, ct);
                Interlocked.Increment(ref processed);
                if (n == 3)
                    sink.Raise("verdict.confirmed");
                return n * 2;
            },
            new EarlyExitOptions<int, int>
            {
                EarlyExitSignals = new HashSet<string> { "verdict.confirmed" },
                OnEarlyExit = (signal, results) => results.Sum()
            },
            new EphemeralOptions { MaxConcurrency = 1, Signals = sink });

        for (var i = 1; i <= 10; i++)
            await coordinator.EnqueueAsync(i);

        var result = await coordinator.DrainAsync();

        Assert.True(result.Exited);
        Assert.Equal("verdict.confirmed", result.ExitSignal);
        // Should have processed only items up to the early exit
        Assert.True(processed < 10);
    }

    [Fact]
    public async Task EarlyExitCoordinator_CompletesNormally()
    {
        var sink = new SignalSink();

        await using var coordinator = new EarlyExitResultCoordinator<int, int>(
            async (n, ct) =>
            {
                await Task.Delay(10, ct);
                return n * 2;
            },
            new EarlyExitOptions<int, int>
            {
                EarlyExitSignals = new HashSet<string> { "early.exit" }
            },
            new EphemeralOptions { MaxConcurrency = 2, Signals = sink });

        await coordinator.EnqueueAsync(1);
        await coordinator.EnqueueAsync(2);
        await coordinator.EnqueueAsync(3);

        var result = await coordinator.DrainAsync();

        Assert.False(result.Exited);
        Assert.Equal(3, result.PartialResults.Count);
    }

    #endregion
}