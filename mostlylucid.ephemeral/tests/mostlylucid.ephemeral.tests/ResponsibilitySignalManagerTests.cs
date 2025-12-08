using System;
using System.Collections.Concurrent;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Signals;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class ResponsibilitySignalManagerTests
{
    [Fact]
    public void PinUntilQueried_UnpinsWhenAckSignalRaised()
    {
        var sink = new SignalSink();
        var pinning = new FakePinning();

        using var manager = new ResponsibilitySignalManager(pinning, sink);
        var operationId = 42L;
        var pinned = manager.PinUntilQueried(operationId, ResponsibilitySignals.DefaultAckPattern);

        Assert.True(pinned);
        Assert.True(pinning.IsPinned(operationId));
        sink.Raise("responsibility.ack.42", key: ResponsibilitySignals.DefaultAckKey(operationId));
        Assert.False(pinning.IsPinned(operationId));
        Assert.Equal(0, manager.PendingCount);
    }

    [Fact]
    public void CompleteResponsibility_UnpinsAndRemovesRegistration()
    {
        var sink = new SignalSink();
        var pinning = new FakePinning();
        using var manager = new ResponsibilitySignalManager(pinning, sink);

        var operationId = 7L;
        Assert.True(manager.PinUntilQueried(operationId, ResponsibilitySignals.DefaultAckPattern));
        Assert.True(pinning.IsPinned(operationId));

        var completed = manager.CompleteResponsibility(operationId);
        Assert.True(completed);
        Assert.False(pinning.IsPinned(operationId));
        Assert.Equal(0, manager.PendingCount);
    }

    [Fact]
    public void PinUntilQueried_ExpiresAfterMaxDuration()
    {
        var sink = new SignalSink();
        var pinning = new FakePinning();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset Clock() => now;

        using var manager = new ResponsibilitySignalManager(
            pinning,
            sink,
            maxPinDuration: TimeSpan.FromSeconds(1),
            clock: Clock);

        var operationId = 100L;
        Assert.True(manager.PinUntilQueried(operationId, ResponsibilitySignals.DefaultAckPattern));
        Assert.True(pinning.IsPinned(operationId));

        now = now.AddSeconds(2);                   // move time forward beyond the max pin duration
        sink.Raise("heartbeat");                   // trigger signal processing which triggers cleanup

        Assert.False(pinning.IsPinned(operationId));
        Assert.Equal(0, manager.PendingCount);
    }

    [Fact]
    public void PinUntilQueried_CreatesSnapshotWithDescription()
    {
        var sink = new SignalSink();
        var pinning = new FakePinning();
        using var manager = new ResponsibilitySignalManager(pinning, sink);

        var operationId = 99L;
        const string description = "Saved file /bucket/uuid";
        Assert.True(manager.PinUntilQueried(operationId, ResponsibilitySignals.DefaultAckPattern, ackKey: ResponsibilitySignals.DefaultAckKey(operationId), description: description));

        var snapshot = Assert.Single(manager.GetActiveResponsibilities());
        Assert.Equal(operationId, snapshot.OperationId);
        Assert.Equal(description, snapshot.Description);
    }

    private sealed class FakePinning : IOperationPinning
    {
        private readonly ConcurrentDictionary<long, bool> _pins = new();

        public bool IsPinned(long operationId) =>
            _pins.TryGetValue(operationId, out var pinned) && pinned;

        public bool Pin(long operationId)
        {
            _pins[operationId] = true;
            return true;
        }

        public bool Unpin(long operationId)
        {
            if (_pins.TryGetValue(operationId, out var pinned) && pinned)
            {
                _pins[operationId] = false;
                return true;
            }
            return false;
        }
    }
}
