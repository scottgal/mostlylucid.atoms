using Mostlylucid.Helpers.Ephemeral;
using Xunit;

namespace Mostlylucid.Test.Ephemeral;

public class ConcurrencyGatesTests
{
    [Fact]
    public async Task Adjustable_IncreaseLimit_ReleasesWaiters()
    {
        await using var gate = new AdjustableConcurrencyGate(1);

        // Take the only permit
        await gate.WaitAsync(CancellationToken.None);

        var blocked = gate.WaitAsync(CancellationToken.None);
        Assert.False(blocked.IsCompleted);

        gate.UpdateLimit(2); // should free the blocked waiter

        await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Adjustable_ReleaseHonorsLimit()
    {
        await using var gate = new AdjustableConcurrencyGate(1);
        await gate.WaitAsync(CancellationToken.None);
        gate.Release(); // returns to 1 available

        // Should not throw or block
        await gate.WaitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Fixed_UpdateLimit_Throws()
    {
        await using var gate = new FixedConcurrencyGate(1);

        Assert.Throws<InvalidOperationException>(() => gate.UpdateLimit(2));
    }
}
