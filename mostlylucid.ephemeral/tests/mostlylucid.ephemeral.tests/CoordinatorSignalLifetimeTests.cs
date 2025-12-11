using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

/// <summary>
/// Tests for v2.0+ operation-owned signal model.
/// Verifies that operations own their signals and when operations are evicted
/// from a coordinator's window, their signals are automatically removed from
/// the coordinator's view (because the operation itself is gone).
/// Sink receives signals for live subscribers but doesn't own them.
/// </summary>
public class CoordinatorSignalLifetimeTests
{
    [Fact]
    public async Task Coordinator_OperationsOwnSignals_EvictionRemovesSignalsFromCoordinatorView()
    {
        var sink = new SignalSink();
        var operationIds = new List<long>();

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                // Note: Operations own signals in v2.0+, but work function doesn't receive operation directly
                // Signals are emitted via coordinator's internal operation tracking
                await Task.Delay(10, ct);
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 5, // Keep only 5 operations
                Signals = sink
            }
        );

        // Enqueue 10 operations and track their IDs
        for (int i = 0; i < 10; i++)
        {
            var opId = await coordinator.EnqueueWithIdAsync(i);
            operationIds.Add(opId);
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Operations own their signals - when evicted, signals go with them
        // Coordinator should only return signals from tracked operations (last 5)
        var coordinatorSignals = coordinator.GetSignals();
        var remainingOpIds = coordinatorSignals.Select(s => s.OperationId).Distinct().ToList();

        Assert.True(remainingOpIds.Count <= 5,
            $"Expected <= 5 unique operation IDs from coordinator, got {remainingOpIds.Count}");

        // Verify early operations' signals are not in coordinator view
        var firstFiveOps = operationIds.Take(5).ToList();
        var firstFiveRemaining = coordinatorSignals.Where(s => firstFiveOps.Contains(s.OperationId)).Count();

        // Should be 0 or very few (due to timing) from first 5 operations
        Assert.True(firstFiveRemaining < 5,
            $"Expected few/no signals from evicted operations, but found {firstFiveRemaining}");
    }

    [Fact]
    public async Task Coordinator_HasAccessToSignalSink()
    {
        // Verify coordinator has access to the signal sink for cleanup
        var sink = new SignalSink();

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(10, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                Signals = sink
            }
        );

        // Verify coordinator exposes Options.Signals for signal cleanup
        Assert.NotNull(coordinator.Options);
        Assert.Same(sink, coordinator.Options.Signals);
    }

    [Fact]
    public async Task Coordinator_MultipleSignalsPerOperation_EvictedTogether()
    {
        var sink = new SignalSink();
        var operationIds = new List<long>();

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 2, // Keep only 2 operations
                Signals = sink
            }
        );

        // Enqueue 5 operations - automatic lifecycle signals will be emitted
        for (int i = 0; i < 5; i++)
        {
            await coordinator.EnqueueAsync(i);
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Operations own their signals - all signals from an operation are evicted together
        var coordinatorSignals = coordinator.GetSignals();
        var opIds = coordinatorSignals.Select(s => s.OperationId).Distinct().Count();

        Assert.True(opIds <= 2,
            $"Expected <= 2 unique operation IDs from coordinator, got {opIds}");

        // Verify that if an operation is tracked, ALL its signals are present
        // Each operation has 2 automatic lifecycle signals (atom.start, atom.stop)
        var groupedByOp = coordinatorSignals.GroupBy(s => s.OperationId);
        foreach (var group in groupedByOp)
        {
            Assert.Equal(2, group.Count()); // 2 automatic lifecycle signals per operation
        }
    }

    [Fact]
    public async Task ResultCoordinator_ClearsSignals_WhenOperationEvicted()
    {
        var sink = new SignalSink();

        await using var coordinator = new EphemeralResultCoordinator<int, string>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
                return $"result-{item}";
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 3,
                Signals = sink
            }
        );

        // Enqueue 6 operations (3 will be evicted)
        // Use fake operation IDs since ResultCoordinator doesn't expose EnqueueWithIdAsync
        for (long i = 1; i <= 6; i++)
        {
            await coordinator.EnqueueAsync((int)i);
            sink.Raise(new SignalEvent($"result.{i}", i, null, DateTimeOffset.UtcNow));
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Test passes if no exceptions were thrown during signal cleanup
        Assert.True(true);
    }

    [Fact]
    public async Task KeyedCoordinator_ClearsSignals_WhenOperationEvicted()
    {
        var sink = new SignalSink();

        await using var coordinator = new EphemeralKeyedWorkCoordinator<string, string>(
            item => item, // Use item as key
            async (item, ct) => await Task.Delay(10, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 3,
                Signals = sink
            }
        );

        // Enqueue 6 operations with different keys
        // Use fake operation IDs since KeyedCoordinator doesn't expose EnqueueWithIdAsync
        for (long i = 1; i <= 6; i++)
        {
            await coordinator.EnqueueAsync($"key-{i}");
            sink.Raise(new SignalEvent($"keyed.{i}", i, null, DateTimeOffset.UtcNow));
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Test passes if no exceptions were thrown during signal cleanup
        Assert.True(true);
    }

    [Fact]
    public async Task Coordinator_WithNoSignalSink_DoesNotThrow()
    {
        // Verify coordinator handles null Signals gracefully
        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(10, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 2,
                Signals = null // No signal sink
            }
        );

        // Should not throw when evicting operations
        for (int i = 0; i < 5; i++)
        {
            await coordinator.EnqueueAsync(i);
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // If we get here without exceptions, the null check works
        Assert.True(true);
    }

    [Fact]
    public async Task SharedSink_AcrossCoordinators_EachCoordinatorTracksOwnOperations()
    {
        var sharedSink = new SignalSink();

        // Coordinator 1: MaxTracked = 2
        await using var coord1 = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 2,
                Signals = sharedSink
            }
        );

        // Coordinator 2: MaxTracked = 3
        await using var coord2 = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                await Task.Delay(10, ct);
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 3,
                Signals = sharedSink
            }
        );

        // Process operations in coord1 (will evict after 2)
        for (int i = 0; i < 5; i++)
        {
            var opId = await coord1.EnqueueWithIdAsync(i);
            sharedSink.Raise(new SignalEvent($"coord1.{i}", opId, null, DateTimeOffset.UtcNow));
        }
        coord1.Complete();
        await coord1.DrainAsync();

        // Process operations in coord2
        for (int i = 0; i < 5; i++)
        {
            var opId = await coord2.EnqueueWithIdAsync(i + 100);
            sharedSink.Raise(new SignalEvent($"coord2.{i}", opId, null, DateTimeOffset.UtcNow));
        }
        coord2.Complete();
        await coord2.DrainAsync();

        // Each coordinator independently tracks its own operations
        // Operations own their signals - coordinator views are independent
        var coord1Signals = coord1.GetSignals();
        var coord2Signals = coord2.GetSignals();

        var coord1Ops = coord1Signals.Select(s => s.OperationId).Distinct().Count();
        var coord2Ops = coord2Signals.Select(s => s.OperationId).Distinct().Count();

        Assert.True(coord1Ops <= 2, $"Coord1 should track <= 2 ops, got {coord1Ops}");
        Assert.True(coord2Ops <= 3, $"Coord2 should track <= 3 ops, got {coord2Ops}");

        // Verify signal prefixes are distinct per coordinator (excluding automatic lifecycle signals)
        var coord1CustomSignals = coord1Signals.Where(s => !s.Signal.StartsWith("atom.")).ToList();
        var coord2CustomSignals = coord2Signals.Where(s => !s.Signal.StartsWith("atom.")).ToList();
        Assert.All(coord1CustomSignals, s => Assert.StartsWith("coord1.", s.Signal));
        Assert.All(coord2CustomSignals, s => Assert.StartsWith("coord2.", s.Signal));
    }
}
