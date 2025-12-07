using Mostlylucid.Helpers.Ephemeral.Atoms;
using Xunit;

namespace Mostlylucid.Test.Ephemeral.Atoms;

public class RetryAtomTests
{
    [Fact]
    public async Task Retries_Until_Succeeds()
    {
        var attempts = 0;
        await using var atom = new RetryAtom<int>(
            async (item, ct) =>
            {
                attempts++;
                if (attempts < 3) throw new InvalidOperationException("fail");
                await Task.CompletedTask;
            },
            maxAttempts: 5,
            backoff: _ => TimeSpan.FromMilliseconds(5));

        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        Assert.Equal(3, attempts);
    }
}
