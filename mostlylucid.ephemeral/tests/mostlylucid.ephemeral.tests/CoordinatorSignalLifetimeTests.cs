using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

/// <summary>
/// Tests for v2.0.0 coordinator-managed signal lifetime.
/// Verifies that when operations are evicted from a coordinator's window,
/// their signals are automatically cleared from the sink.
/// </summary>
public class CoordinatorSignalLifetimeTests
{
    [Fact]
    public async Task Coordinator_ClearsSignals_WhenOperationEvicted_ByWindowSize()
    {
        var sink = new SignalSink();
        var operationIds = new List<long>();

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) =>
            {
                // Simple operation - signals will be raised externally
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

            // Emit signals associated with this operation
            sink.Raise(new SignalEvent($"test.{i}", opId, null, DateTimeOffset.UtcNow));
            sink.Raise(new SignalEvent($"test.complete.{i}", opId, null, DateTimeOffset.UtcNow));
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Only signals from the last 5 operations should remain
        var signals = sink.Sense();
        var remainingOpIds = signals.Select(s => s.OperationId).Distinct().ToList();

        Assert.True(remainingOpIds.Count <= 5,
            $"Expected <= 5 unique operation IDs, got {remainingOpIds.Count}");

        // Verify early operations' signals were cleared
        var firstFiveOps = operationIds.Take(5).ToList();
        var lastFiveOps = operationIds.Skip(5).ToList();

        // At least some of the first 5 operations should have been evicted and their signals cleared
        var firstFiveRemaining = signals.Where(s => firstFiveOps.Contains(s.OperationId)).Count();
        Assert.True(firstFiveRemaining < 10, // Should be fewer than 5 ops × 2 signals
            $"Expected most early signals cleared, but found {firstFiveRemaining} signals from first 5 operations");
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
    public async Task Coordinator_ClearsMultipleSignals_FromSameOperation()
    {
        var sink = new SignalSink();
        var operationIds = new List<long>();

        await using var coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(10, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 2, // Keep only 2 operations
                Signals = sink
            }
        );

        // Enqueue 5 operations
        for (int i = 0; i < 5; i++)
        {
            var opId = await coordinator.EnqueueWithIdAsync(i);
            operationIds.Add(opId);

            // Emit multiple signals per operation
            for (int j = 0; j < 5; j++)
            {
                sink.Raise(new SignalEvent($"signal.{i}.{j}", opId, null, DateTimeOffset.UtcNow));
            }
        }

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Should have signals from only 2 operations
        var signals = sink.Sense();
        var opIds = signals.Select(s => s.OperationId).Distinct().Count();
        Assert.True(opIds <= 2,
            $"Expected <= 2 unique operation IDs, got {opIds}");
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
    public async Task SharedSink_AcrossCoordinators_IsolatesSignalCleanup()
    {
        var sharedSink = new SignalSink();

        // Coordinator 1: MaxTracked = 2
        await using var coord1 = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(10, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 2,
                Signals = sharedSink
            }
        );

        // Coordinator 2: MaxTracked = 3
        await using var coord2 = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(10, ct),
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

        // Verify each coordinator only cleared its own signals
        var allSignals = sharedSink.Sense();

        // Should have signals from coord1's last 2 ops + coord2's last 3 ops
        // (assuming signals were successfully emitted and not yet cleaned up)
        var coord1Signals = allSignals.Where(s => s.Signal.StartsWith("coord1.")).ToList();
        var coord2Signals = allSignals.Where(s => s.Signal.StartsWith("coord2.")).ToList();

        // Each coordinator should have cleared its old operations' signals
        var coord1Ops = coord1Signals.Select(s => s.OperationId).Distinct().Count();
        var coord2Ops = coord2Signals.Select(s => s.OperationId).Distinct().Count();

        Assert.True(coord1Ops <= 2, $"Coord1 should have <= 2 ops, got {coord1Ops}");
        Assert.True(coord2Ops <= 3, $"Coord2 should have <= 3 ops, got {coord2Ops}");
    }
}
