using System.Threading;
using Mostlylucid.Helpers.Ephemeral;
using Xunit;

namespace Mostlylucid.Test.Ephemeral;

public class SignalDispatcherTests
{
    [Fact]
    public async Task Dispatch_RoutesByPattern()
    {
        var hits = new List<string>();
        await using var dispatcher = new SignalDispatcher(new EphemeralOptions
        {
            MaxConcurrency = 4,
            MaxConcurrencyPerKey = 1,
            MaxTrackedOperations = 32
        });

        dispatcher.Register("db.*", evt =>
        {
            hits.Add(evt.Signal);
            return Task.CompletedTask;
        });

        dispatcher.Register("cache.hit", evt =>
        {
            hits.Add("cache");
            return Task.CompletedTask;
        });

        dispatcher.Dispatch(new SignalEvent("db.query", 1, null, DateTimeOffset.UtcNow));
        dispatcher.Dispatch(new SignalEvent("cache.hit", 2, null, DateTimeOffset.UtcNow));
        dispatcher.Dispatch(new SignalEvent("http", 3, null, DateTimeOffset.UtcNow));

        await Task.Delay(50); // allow dispatch to complete

        Assert.Contains("db.query", hits);
        Assert.Contains("cache", hits);
        Assert.DoesNotContain("http", hits);
    }

    [Fact]
    public async Task Dispatch_MultipleHandlers_RunInRegistrationOrder()
    {
        var hits = new List<string>();
        await using var dispatcher = new SignalDispatcher(new EphemeralOptions
        {
            MaxConcurrency = 2,
            MaxConcurrencyPerKey = 1,
            MaxTrackedOperations = 32
        });

        dispatcher.Register("db.*", evt =>
        {
            hits.Add("first");
            return Task.CompletedTask;
        });

        dispatcher.Register("*", evt =>
        {
            hits.Add("catch-all");
            return Task.CompletedTask;
        });

        dispatcher.Dispatch(new SignalEvent("db.query", 1, null, DateTimeOffset.UtcNow));
        await Task.Delay(50);

        Assert.Equal(new[] { "first", "catch-all" }, hits);
    }

    [Fact]
    public async Task Dispatch_ReturnsFalseAfterDispose()
    {
        var hits = 0;
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new SignalDispatcher();
        dispatcher.Register("*", _ =>
        {
            Interlocked.Increment(ref hits);
            dispatched.TrySetResult();
            return Task.CompletedTask;
        });

        dispatcher.Dispatch(new SignalEvent("warmup", 1, null, DateTimeOffset.UtcNow));
        await dispatched.Task.WaitAsync(TimeSpan.FromMilliseconds(100));
        await dispatcher.DisposeAsync();

        var enqueued = dispatcher.Dispatch(new SignalEvent("after", 2, null, DateTimeOffset.UtcNow));
        Assert.False(enqueued);
        await Task.Delay(20);
        Assert.Equal(1, hits);
    }
}
