using Mostlylucid.Ephemeral.Atoms.Molecules;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public sealed class MoleculeRunnerTests
{
    [Fact]
    public async Task ExecutesBlueprintWhenSignalMatches()
    {
        var sink = new SignalSink();
        var steps = new List<string>();
        var blueprint = new MoleculeBlueprintBuilder("order", "order.*")
            .AddAtom(async (ctx, ct) =>
            {
                steps.Add("payment");
                await Task.Delay(1, ct).ConfigureAwait(false);
            })
            .Build();

        blueprint.AddAtom((ctx, _) =>
        {
            steps.Add("inventory");
            return Task.CompletedTask;
        });

        await using var runner = new MoleculeRunner(sink, new[] { blueprint });
        var tcs = new TaskCompletionSource<bool>();
        runner.MoleculeCompleted += (_, _) => tcs.TrySetResult(true);

        sink.Raise("order.placed", "order-1");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "payment", "inventory" }, steps);
    }

    [Fact]
    public async Task RemovesAtomStepBeforeExecution()
    {
        var sink = new SignalSink();
        var steps = new List<string>();
        var inventoryStep = new Func<MoleculeContext, CancellationToken, Task>((ctx, _) =>
        {
            steps.Add("inventory");
            return Task.CompletedTask;
        });

        var blueprint = new MoleculeBlueprintBuilder("order", "order.*")
            .AddAtom(async (ctx, ct) =>
            {
                steps.Add("payment");
                await Task.Delay(1, ct).ConfigureAwait(false);
            })
            .AddAtom(inventoryStep)
            .Build();

        blueprint.RemoveAtoms(step => step == inventoryStep);
        await using var runner = new MoleculeRunner(sink, new[] { blueprint });
        var tcs = new TaskCompletionSource<bool>();
        runner.MoleculeCompleted += (_, _) => tcs.TrySetResult(true);

        sink.Raise("order.placed", "order-1");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(steps, "payment");
    }
}