using Xunit;

namespace Mostlylucid.Ephemeral.Atoms.Taxonomy.Ranker.Tests;

public class RankerAtomTests
{
    [Fact]
    public async Task DefaultContractMatchesKind()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);

        await using var atom = new RankerAtom<int, int>(typed, Handler, maxBodyDuration: TimeSpan.FromSeconds(30));

        Assert.Equal(AtomKind.Ranker, atom.Contract.Kind);
        Assert.Equal(AtomDeterminism.Deterministic, atom.Contract.Determinism);
        Assert.Equal(AtomPersistence.EphemeralOnly, atom.Contract.Persistence);
    }

    [Fact]
    public async Task DefaultOutputSignalMatchesKind()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);

        await using var atom = new RankerAtom<int, int>(typed, Handler, maxBodyDuration: TimeSpan.FromSeconds(30));

        var expected = "atom.ranker.output";
        Assert.Equal(expected, atom.OutputSignal);
    }

    [Fact]
    public async Task RunAsyncEmitsTypedSignalAndReturnsOutput()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var tcs = new TaskCompletionSource<SignalEvent<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        typed.TypedSignalRaised += evt => tcs.TrySetResult(evt);

        await using var atom = new RankerAtom<int, int>(typed, Handler, maxBodyDuration: TimeSpan.FromSeconds(30), outputSignal: "ranker.output");
        var result = await atom.RunAsync(5);

        Assert.Equal(6, result);
        var evt = await WaitForSignalAsync(tcs.Task);
        Assert.Equal("ranker.output", evt.Signal);
        Assert.Equal(6, evt.Payload);
    }

    [Fact]
    public async Task KeySelectorIsUsedForOutputSignals()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var tcs = new TaskCompletionSource<SignalEvent<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        typed.TypedSignalRaised += evt => tcs.TrySetResult(evt);

        await using var atom = new RankerAtom<int, int>(
            typed,
            Handler,
            maxBodyDuration: TimeSpan.FromSeconds(30),
            outputSignal: "ranker.output",
            keySelector: value => $"key-{value}");

        await atom.RunAsync(3);
        var evt = await WaitForSignalAsync(tcs.Task);
        Assert.Equal("key-3", evt.Key);
    }

    [Fact]
    public async Task EmitOutputSignalsFalseSuppressesEmission()
    {
        var sink = new SignalSink();
        var typed = new TypedSignalSink<int>(sink);
        var tcs = new TaskCompletionSource<SignalEvent<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        typed.TypedSignalRaised += evt => tcs.TrySetResult(evt);

        await using var atom = new RankerAtom<int, int>(typed, Handler, maxBodyDuration: TimeSpan.FromSeconds(30), emitOutputSignals: false);
        await atom.RunAsync(1);

        await Task.Delay(100);
        Assert.False(tcs.Task.IsCompleted);
        Assert.Null(atom.OutputSignal);
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