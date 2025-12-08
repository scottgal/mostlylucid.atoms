using System;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Volatile;

public sealed class VolatileOperationAtomOptions
{
    /// <summary>
    ///     Signal pattern that marks an operation as volatile (default: <c>kill.*</c>).
    /// </summary>
    public string KillSignalPattern { get; init; } = "kill.*";

    /// <summary>
    ///     Optional hook that runs when a signal matches the kill criteria.
    ///     Return <see langword="true"/> to treat the signal as a kill even if it does not match <see cref="KillSignalPattern"/>.
    /// </summary>
    public Func<SignalEvent, bool>? ShouldKill { get; init; }

    /// <summary>
    ///     Called when the operation was found and evicted.
    /// </summary>
    public Action<SignalEvent>? OnKilled { get; init; }

    /// <summary>
    ///     Called when the kill signal was seen but no matching operation was still tracked.
    /// </summary>
    public Action<SignalEvent>? OnNotFound { get; init; }
}
