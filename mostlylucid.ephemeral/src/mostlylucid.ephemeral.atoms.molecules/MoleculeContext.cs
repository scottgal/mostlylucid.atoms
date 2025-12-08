using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Molecules;

/// <summary>
/// Runtime context provided to each atom inside a molecule.
/// </summary>
public sealed class MoleculeContext
{
    internal MoleculeContext(SignalEvent trigger, SignalSink signals, IServiceProvider services, CancellationToken token)
    {
        TriggerSignal = trigger;
        Signals = signals;
        Services = services;
        CancellationToken = token;
        SharedState = new ConcurrentDictionary<string, object?>();
    }

    /// <summary>
    /// Signal that started the molecule.
    /// </summary>
    public SignalEvent TriggerSignal { get; }

    /// <summary>
    /// Signal sink shared with the coordinator.
    /// </summary>
    public SignalSink Signals { get; }

    /// <summary>
    /// Services resolved for hosting atoms.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Cancellation token for the molecule work.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Arbitrary state transmission between atoms in the molecule.
    /// </summary>
    public IDictionary<string, object?> SharedState { get; }

    /// <summary>
    /// Raise a signal inside the molecule.
    /// </summary>
    public void Raise(string signal, string? key = null) => Signals.Raise(signal, key);
}
