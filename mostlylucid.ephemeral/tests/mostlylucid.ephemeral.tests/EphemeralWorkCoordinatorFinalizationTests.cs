using System;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class EphemeralWorkCoordinatorFinalizationTests
{
    [Fact]
    public async Task OperationFinalized_EventFiresWhenWindowOverflows()
    {
        var options = new EphemeralOptions
        {
            MaxTrackedOperations = 1
        };

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            (item, ct) => Task.CompletedTask,
            options);

        var tcs = new TaskCompletionSource<EphemeralOperationSnapshot>();
        coordinator.OperationFinalized += snapshot =>
        {
            tcs.TrySetResult(snapshot);
        };

        var firstId = await coordinator.EnqueueWithIdAsync(1);
        await coordinator.EnqueueAsync(2);

        var finalized = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(firstId, finalized.OperationId);
    }
}
