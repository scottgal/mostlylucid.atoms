using Xunit;

namespace Mostlylucid.Ephemeral.Atoms.Taxonomy.Tests;

public class MultiTaxonomyAtomTests
{
    [Fact]
    public async Task ContractComposesKindsAndCapabilities()
    {
        var shards = new[]
        {
            TaxonomyShard.Create<CoordinatorShard>(),
            TaxonomyShard.Create<EscalatorShard>()
        };

        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);

        await using var atom = new MultiTaxonomyAtom<int, int>(typed, Handler, shards, TimeSpan.FromSeconds(30));

        Assert.Equal(AtomKind.Coordinator, atom.Contract.Kind);
        Assert.Equal(AtomDeterminism.Deterministic, atom.Contract.Determinism);
        Assert.Equal(AtomPersistence.DirectWriteAllowed, atom.Contract.Persistence);
        Assert.Contains(AtomKind.Coordinator, atom.Contract.Kinds);
        Assert.Contains(AtomKind.Escalator, atom.Contract.Kinds);
    }

    [Fact]
    public async Task DefaultOutputSignalUsesPrimaryShard()
    {
        var shards = new[]
        {
            TaxonomyShard.Create<RankerShard>(),
            TaxonomyShard.Create<GuardShard>()
        };

        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);

        await using var atom = new MultiTaxonomyAtom<int, int>(typed, Handler, shards, TimeSpan.FromSeconds(30));

        Assert.Equal("atom.ranker.output", atom.OutputSignal);
    }

    [Fact]
    public async Task RunAsyncEmitsTypedSignalAndReturnsOutput()
    {
        var shards = new[]
        {
            TaxonomyShard.Create<SensorShard>(),
            TaxonomyShard.Create<ExtractorShard>()
        };

        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var tcs = new TaskCompletionSource<SignalEvent<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        typed.TypedSignalRaised += evt => tcs.TrySetResult(evt);

        await using var atom = new MultiTaxonomyAtom<int, int>(typed, Handler, shards, TimeSpan.FromSeconds(30),
            "multi.output");
        var result = await atom.RunAsync(5);

        Assert.Equal(6, result);
        var evt = await WaitForSignalAsync(tcs.Task);
        Assert.Equal("multi.output", evt.Signal);
        Assert.Equal(6, evt.Payload);
    }

    private static Task<int> Handler(int input, CancellationToken _)
    {
        return Task.FromResult(input + 1);
    }

    private static async Task<T> WaitForSignalAsync<T>(Task<T> task, int timeoutMs = 1000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(completed == task, "Timed out waiting for signal.");
        return await task;
    }
}