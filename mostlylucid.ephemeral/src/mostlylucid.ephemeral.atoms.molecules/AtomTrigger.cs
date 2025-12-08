using System;
using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Molecules;

/// <summary>
/// Hooks a signal pattern to an action that starts another atom/coordinator.
/// </summary>
public sealed class AtomTrigger : IDisposable
{
    private readonly SignalSink _signals;
    private readonly Func<string, bool> _matcher;
    private readonly Func<SignalEvent, CancellationToken, Task> _action;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Creates a trigger that runs the provided action when signals match the pattern.
    /// </summary>
    public AtomTrigger(
        SignalSink signals,
        string signalPattern,
        Func<SignalEvent, CancellationToken, Task> action)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        if (string.IsNullOrWhiteSpace(signalPattern))
            throw new ArgumentException("Signal pattern is required.", nameof(signalPattern));
        _matcher = signal => StringPatternMatcher.Matches(signal, signalPattern);
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _signals.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent evt)
    {
        if (_cts.IsCancellationRequested || !_matcher(evt.Signal))
            return;

        _ = Task.Run(() => _action(evt, _cts.Token), _cts.Token);
    }

    /// <summary>
    /// Stop listening for signals.
    /// </summary>
    public void Dispose()
    {
        _signals.SignalRaised -= OnSignal;
        _cts.Cancel();
        _cts.Dispose();
    }
}
