using System;
using System.Collections.Generic;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Echo;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public sealed class OperationEchoMakerTests
{
    [Fact]
    public void ActivationSignalTriggersEchoCapture()
    {
        var typedSink = new TypedSignalSink<EchoPayload>();
        var finalizer = new TestFinalizer();
        var echoes = new List<OperationEchoEntry<EchoPayload>>();

        using var maker = new OperationEchoMaker<EchoPayload>(
            typedSink,
            finalizer,
            echoes.Add,
            new OperationEchoMakerOptions<EchoPayload>
            {
                ActivationSignalPattern = "echo.capture",
                CaptureSignalPattern = "echo.*"
            });

        typedSink.Raise(new SignalEvent<EchoPayload>("echo.capture", 42, "order-1", DateTimeOffset.UtcNow, new EchoPayload("order-1", "ready")));
        typedSink.Raise(new SignalEvent<EchoPayload>("echo.state", 42, "order-1", DateTimeOffset.UtcNow, new EchoPayload("order-1", "finalizing")));

        var snapshot = new EphemeralOperationSnapshot(
            Id: 42,
            Started: DateTimeOffset.UtcNow.AddSeconds(-5),
            Completed: DateTimeOffset.UtcNow,
            Key: "order-1",
            IsFaulted: false,
            Error: null,
            Duration: TimeSpan.FromSeconds(5),
            Signals: null,
            IsPinned: false);

        finalizer.Emit(snapshot);

        Assert.Single(echoes);
        Assert.Equal(42, echoes[0].OperationId);
        Assert.Contains(echoes[0].Captures, c => c.Signal == "echo.state");
    }

    private sealed record EchoPayload(string OrderId, string Description);

    private sealed class TestFinalizer : IOperationFinalization
    {
        public event Action<EphemeralOperationSnapshot>? OperationFinalized;

        public void Emit(EphemeralOperationSnapshot snapshot) => OperationFinalized?.Invoke(snapshot);
    }
}
