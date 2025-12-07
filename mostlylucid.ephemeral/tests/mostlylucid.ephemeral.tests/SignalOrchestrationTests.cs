using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class SignalOrchestrationTests
{
    [Fact]
    public void TypedSignalSink_EmitsTypedAndUntyped()
    {
        var untyped = new SignalSink();
        var typed = new TypedSignalSink<string>(untyped);
        var raised = 0;
        untyped.SignalRaised += _ => Interlocked.Increment(ref raised);

        typed.Raise("bot.evidence", "payload", "k1");

        var typedSignals = typed.Sense();
        Assert.Single(typedSignals);
        Assert.Equal("payload", typedSignals[0].Payload);
        Assert.True(untyped.Detect("bot.evidence"));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SignalConsensus_WaitsForQuorum()
    {
        var sink = new SignalSink();
        var waitTask = SignalConsensus.WaitForQuorumAsync(sink, "vote.*", required: 3, timeout: TimeSpan.FromSeconds(2));

        sink.Raise("vote.a");
        sink.Raise("vote.b");
        sink.Raise("vote.c");

        var result = await waitTask;
        Assert.True(result.Reached);
        Assert.False(result.TimedOut);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task SignalWaveExecutor_EarlyExitCancelsStages()
    {
        var sink = new SignalSink();
        var cancelled = false;
        var stage = new SignalStage(
            "detect",
            "stage.start",
            async ct =>
            {
                try
                {
                    await Task.Delay(1_000, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            },
            EmitOnStart: new[] { "stage.detect.start" },
            EmitOnComplete: new[] { "stage.detect.complete" });

        await using var executor = new SignalWaveExecutor(sink, new[] { stage }, earlyExitSignals: new[] { "early.exit" }, maxConcurrentStages: 1);
        executor.Start();

        sink.Raise("stage.start");
        await Task.Delay(50);
        sink.Raise("early.exit");
        await Task.Delay(150);

        Assert.True(cancelled);
        Assert.True(sink.Detect(s => s.Signal.StartsWith("stage.exit")));
    }

    [Fact]
    public void DecayingReputationWindow_DecaysScores()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var window = new DecayingReputationWindow<string>(TimeSpan.FromSeconds(60), clock: () => clock);

        var initial = window.Update("user", 10);
        Assert.InRange(initial, 9.9, 10.1);

        clock = now.AddSeconds(60);
        var decayed = window.GetScore("user");
        Assert.InRange(decayed, 4.9, 5.1); // half-life at 60s

        var boosted = window.Update("user", 10);
        Assert.InRange(boosted, 14.9, 15.2); // decayed 5 + 10
    }

    [Fact]
    public void ProgressSignals_SamplesAndAlwaysEmitsFinal()
    {
        var sink = new SignalSink();

        for (var i = 1; i <= 5; i++)
        {
            ProgressSignals.Emit(sink, "work", i, 5, sampleRate: 2);
        }

        var signals = sink.Sense(s => s.Signal.StartsWith("progress:")).Count;
        Assert.Equal(3, signals); // 2, 4 (sampled) and 5 (final)
    }
}
