using System.Collections.Concurrent;
using Xunit;

namespace Mostlylucid.Ephemeral.Atoms.Escalator.Tests;

public class EscalatorAtomTests
{
    [Fact]
    public async Task EscalatorPersistsToAllTargetsAndEmitsSuccess()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var calls = new ConcurrentBag<string>();
        var successTcs = new TaskCompletionSource<SignalEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = sink.Subscribe(evt =>
        {
            if (evt.Signal == "escalation.persisted")
                successTcs.TrySetResult(evt);
        });

        var targets = new[]
        {
            new EscalationTarget<int>("primary", (evt, ct) =>
            {
                calls.Add("primary");
                return Task.CompletedTask;
            }),
            new EscalationTarget<int>("audit", (evt, ct) =>
            {
                calls.Add("audit");
                return Task.CompletedTask;
            })
        };

        await using var escalator = new EscalatorAtom<int>(sink, typed, targets, TimeSpan.FromSeconds(30));
        typed.Raise("escalate.signal", 5, "order-1");

        await WaitForSignalAsync(successTcs.Task);
        Assert.Contains("primary", calls);
        Assert.Contains("audit", calls);
    }

    [Fact]
    public async Task EscalatorSkipsNonMatchingSignals()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var calls = new ConcurrentBag<string>();

        var targets = new[]
        {
            new EscalationTarget<int>("primary", (evt, ct) =>
            {
                calls.Add("primary");
                return Task.CompletedTask;
            })
        };

        await using var escalator = new EscalatorAtom<int>(
            sink,
            typed,
            targets,
            TimeSpan.FromSeconds(30),
            new EscalatorAtomOptions<int> { EscalateSignalPattern = "escalate.*" });

        typed.Raise("ignore.signal", 1);
        await Task.Delay(100);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task EscalatorEmitsFailureSignalWhenTargetThrows()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var calls = new ConcurrentBag<string>();
        var failureTcs = new TaskCompletionSource<SignalEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = sink.Subscribe(evt =>
        {
            if (evt.Signal.StartsWith("escalation.failed"))
                failureTcs.TrySetResult(evt);
        });

        var targets = new[]
        {
            new EscalationTarget<int>("primary", (evt, ct) =>
            {
                calls.Add("primary");
                return Task.CompletedTask;
            }),
            new EscalationTarget<int>("faulty", (evt, ct) =>
            {
                calls.Add("faulty");
                throw new InvalidOperationException("boom");
            })
        };

        await using var escalator = new EscalatorAtom<int>(sink, typed, targets, TimeSpan.FromSeconds(30));
        typed.Raise("escalate.fail", 42, "order-2");

        var failure = await WaitForSignalAsync(failureTcs.Task);
        Assert.Contains("InvalidOperationException", failure.Signal);
        Assert.Contains("primary", calls);
        Assert.Contains("faulty", calls);
    }

    [Fact]
    public async Task ShouldEscalateOverridesPattern()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var calls = new ConcurrentBag<string>();

        var targets = new[]
        {
            new EscalationTarget<int>("primary", (evt, ct) =>
            {
                calls.Add("primary");
                return Task.CompletedTask;
            })
        };

        var escalator = new EscalatorAtom<int>(
            sink,
            typed,
            targets,
            TimeSpan.FromSeconds(30),
            new EscalatorAtomOptions<int>
            {
                EscalateSignalPattern = "escalate.*",
                ShouldEscalate = evt => evt.Signal == "force.persist"
            });

        typed.Raise("force.persist", 7);
        await escalator.DisposeAsync();

        Assert.Contains("primary", calls);
    }

    private static async Task<T> WaitForSignalAsync<T>(Task<T> task, int timeoutMs = 1000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(completed == task, "Timed out waiting for signal.");
        return await task;
    }
}