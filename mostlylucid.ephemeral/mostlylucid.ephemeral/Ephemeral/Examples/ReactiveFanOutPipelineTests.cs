using System.Threading;
using Mostlylucid.Helpers.Ephemeral;
using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Examples;

public class ReactiveFanOutPipelineTests
{
    [Fact]
    public async Task Throttles_On_Backpressure_And_Recovers()
    {
        var sink = new SignalSink(maxCapacity: 1024, maxAge: TimeSpan.FromSeconds(5));

        await using var pipeline = new ReactiveFanOutPipeline<int>(
            stage2Work: async (_, ct) =>
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            },
            stage1MaxConcurrency: 8,
            stage1MinConcurrency: 1,
            stage2MaxConcurrency: 1,
            backpressureThreshold: 3,
            reliefThreshold: 1,
            adjustCooldownMs: 50,
            sink: sink);

        // First burst should trigger backpressure due to slow stage 2.
        for (var i = 0; i < 30; i++)
            await pipeline.EnqueueAsync(i);

        var throttled = SpinWait.SpinUntil(() => pipeline.Stage1CurrentMaxConcurrency == 1, TimeSpan.FromSeconds(2));
        Assert.True(throttled, "Stage 1 should drop to min concurrency under backpressure");
        Assert.True(sink.Detect("stage2.backpressure"), "Backpressure signal should be raised");

        // Let backlog drain, then enqueue a small follow-up to allow recovery.
        var drained = SpinWait.SpinUntil(() => pipeline.Stage2Pending == 0, TimeSpan.FromSeconds(3));
        Assert.True(drained, "Stage 2 should drain");

        for (var i = 100; i < 102; i++)
            await pipeline.EnqueueAsync(i);

        var recovered = SpinWait.SpinUntil(() => pipeline.Stage1CurrentMaxConcurrency == 8, TimeSpan.FromSeconds(2));
        Assert.True(recovered, "Stage 1 should recover to max concurrency once backpressure clears");

        await pipeline.DrainAsync();
    }
}
