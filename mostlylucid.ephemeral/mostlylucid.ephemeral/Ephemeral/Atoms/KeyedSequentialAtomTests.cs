using System.Collections.Concurrent;
using Mostlylucid.Helpers.Ephemeral.Atoms;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Atoms;

public class KeyedSequentialAtomTests
{
    [Fact]
    public async Task Preserves_Key_Order()
    {
        var order = new ConcurrentQueue<(string Key, int Value)>();
        await using var atom = new KeyedSequentialAtom<(string Key, int Value), string>(
            item => item.Key,
            async (item, ct) =>
            {
                await Task.Delay(2, ct);
                order.Enqueue(item);
            },
            maxConcurrency: 4,
            perKeyConcurrency: 1);

        var inputs = new[]
        {
            ("a", 1), ("b", 1), ("a", 2), ("b", 2), ("a", 3)
        };

        foreach (var input in inputs)
        {
            await atom.EnqueueAsync(input);
        }

        await atom.DrainAsync();

        var snapshot = order.ToArray();
        var aValues = snapshot.Where(x => x.Key == "a").Select(x => x.Value).ToArray();
        var bValues = snapshot.Where(x => x.Key == "b").Select(x => x.Value).ToArray();

        Assert.Equal(new[] { 1, 2, 3 }, aValues);
        Assert.Equal(new[] { 1, 2 }, bValues);
    }
}
