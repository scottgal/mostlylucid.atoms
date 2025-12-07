using Mostlylucid.Helpers.Ephemeral.Atoms;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Atoms;

public class BatchingAtomTests
{
    [Fact]
    public async Task Flushes_On_Size_And_Interval()
    {
        var batches = new List<IReadOnlyList<int>>();
        await using var atom = new BatchingAtom<int>(
            async (items, ct) =>
            {
                batches.Add(items.ToArray());
                await Task.CompletedTask;
            },
            maxBatchSize: 3,
            flushInterval: TimeSpan.FromMilliseconds(50));

        atom.Enqueue(1);
        atom.Enqueue(2);
        atom.Enqueue(3); // triggers size flush

        await Task.Delay(20); // allow flush
        atom.Enqueue(4);

        await Task.Delay(80); // interval flush for leftover

        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { 1, 2, 3 }, batches[0]);
        Assert.Equal(new[] { 4 }, batches[1]);
    }
}
