using System;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Volatile;

public sealed class VolatileOperationAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly IOperationEvictor _evictor;
    private readonly VolatileOperationAtomOptions _options;
    private bool _disposed;

    public VolatileOperationAtom(SignalSink signals, IOperationEvictor evictor, VolatileOperationAtomOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _evictor = evictor ?? throw new ArgumentNullException(nameof(evictor));
        _options = options ?? new VolatileOperationAtomOptions();
        _signals.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent signal)
    {
        if (_disposed || signal.OperationId <= 0)
            return;

        var shouldKill = _options.ShouldKill is not null
            ? _options.ShouldKill(signal)
            : StringPatternMatcher.Matches(signal.Signal, _options.KillSignalPattern);

        if (!shouldKill)
            return;

        if (_evictor.TryKill(signal.OperationId))
            _options.OnKilled?.Invoke(signal);
        else
            _options.OnNotFound?.Invoke(signal);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return default;

        _disposed = true;
        _signals.SignalRaised -= OnSignal;
        return default;
    }
}
