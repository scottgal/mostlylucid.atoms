using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     A signal event raised by an operation.
///     Readonly struct for zero-allocation hot path (~48 bytes on stack, no GC pressure).
/// </summary>
public readonly record struct SignalEvent(
    string Signal,
    long OperationId,
    string? Key,
    DateTimeOffset Timestamp,
    SignalPropagation? Propagation = null)
{
    /// <summary>
    ///     Current propagation depth (0 = root signal).
    /// </summary>
    public int Depth => Propagation?.Depth ?? 0;

    /// <summary>
    ///     Check if emitting a signal would create a cycle.
    /// </summary>
    public bool WouldCycle(string signal)
    {
        return Propagation?.Contains(signal) == true;
    }

    /// <summary>
    ///     Check if emitting a signal would create a cycle (span overload for zero-allocation).
    /// </summary>
    public bool WouldCycle(ReadOnlySpan<char> signal)
    {
        return Propagation?.Contains(signal) == true;
    }

    /// <summary>
    ///     Check if this signal matches a name (exact match).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Is(string name)
    {
        return Signal == name;
    }

    /// <summary>
    ///     Check if this signal matches a name (span overload for zero-allocation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Is(ReadOnlySpan<char> name)
    {
        return Signal.AsSpan().SequenceEqual(name);
    }

    /// <summary>
    ///     Check if this signal starts with a prefix (e.g., "http." matches "http.complete").
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool StartsWith(string prefix)
    {
        return Signal.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Check if this signal starts with a prefix (span overload for zero-allocation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool StartsWith(ReadOnlySpan<char> prefix)
    {
        return Signal.AsSpan().StartsWith(prefix);
    }
}

/// <summary>
///     Event raised when a signal is retracted (removed) from an operation.
/// </summary>
public readonly record struct SignalRetractedEvent(
    string Signal,
    long OperationId,
    string? Key,
    DateTimeOffset Timestamp,
    bool WasPatternMatch = false,
    string? Pattern = null)
{
    /// <summary>
    ///     Check if this retraction matches a signal name (exact match).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Is(string name)
    {
        return Signal == name;
    }
}

/// <summary>
///     Tracks signal propagation for cycle detection and depth limiting.
///     Immutable, allocation-efficient for shallow depths.
/// </summary>
public sealed class SignalPropagation
{
    private readonly string[] _path;

    private SignalPropagation(string[] path)
    {
        _path = path;
    }

    /// <summary>
    ///     Current depth in the propagation chain.
    /// </summary>
    public int Depth => _path.Length;

    /// <summary>
    ///     The causal chain of signals that led here.
    /// </summary>
    public IReadOnlyList<string> Path => _path;

    /// <summary>
    ///     Create the root of a propagation chain.
    /// </summary>
    public static SignalPropagation Root(string signal)
    {
        return new SignalPropagation([signal]);
    }

    /// <summary>
    ///     Extend the chain with a new signal.
    /// </summary>
    public SignalPropagation Extend(string signal)
    {
        var newPath = new string[_path.Length + 1];
        _path.CopyTo(newPath, 0);
        newPath[_path.Length] = signal;
        return new SignalPropagation(newPath);
    }

    /// <summary>
    ///     Check if a signal is already in the propagation path (would cycle).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(string signal)
    {
        foreach (var s in _path)
            if (s == signal)
                return true;
        return false;
    }

    /// <summary>
    ///     Check if a signal is already in the propagation path (span overload for zero-allocation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ReadOnlySpan<char> signal)
    {
        foreach (var s in _path)
            if (s.AsSpan().SequenceEqual(signal))
                return true;
        return false;
    }

    /// <summary>
    ///     Get the path as a readable string for debugging.
    /// </summary>
    public override string ToString()
    {
        return string.Join(" -> ", _path);
    }
}

/// <summary>
///     Constraints for signal propagation to prevent infinite loops and runaway chains.
/// </summary>
public sealed class SignalConstraints
{
    /// <summary>
    ///     Maximum propagation depth before signals are blocked.
    ///     Default: 10 (prevents deep cascades).
    /// </summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>
    ///     Whether to block signals that would create cycles.
    ///     Default: true (prevents infinite loops).
    /// </summary>
    public bool BlockCycles { get; init; } = true;

    /// <summary>
    ///     Terminal signals that end propagation chains.
    ///     Any signal in this set will not propagate further.
    ///     Use for "final state" signals like "resolved", "failed", "completed".
    /// </summary>
    public IReadOnlySet<string>? TerminalSignals { get; init; }

    /// <summary>
    ///     Signals that cannot trigger further signals (one-way).
    ///     Unlike terminal signals, these can still be emitted but won't carry propagation.
    /// </summary>
    public IReadOnlySet<string>? LeafSignals { get; init; }

    /// <summary>
    ///     Optional callback when a signal is blocked due to constraints.
    /// </summary>
    public Action<SignalEvent, SignalBlockReason>? OnBlocked { get; init; }

    /// <summary>
    ///     Default constraints: depth 10, block cycles, no terminal/leaf signals.
    /// </summary>
    public static SignalConstraints Default { get; } = new();

    /// <summary>
    ///     Strict constraints: depth 5, block cycles.
    /// </summary>
    public static SignalConstraints Strict { get; } = new() { MaxDepth = 5 };

    /// <summary>
    ///     No constraints (use with caution!).
    /// </summary>
    public static SignalConstraints None { get; } = new() { MaxDepth = int.MaxValue, BlockCycles = false };

    /// <summary>
    ///     Check if a signal emission should be allowed given current propagation.
    /// </summary>
    public SignalBlockReason? ShouldBlock(string signal, SignalPropagation? propagation)
    {
        if (propagation is null)
            return null; // Root signals always allowed

        // Check depth
        if (propagation.Depth >= MaxDepth)
            return SignalBlockReason.MaxDepthExceeded;

        // Check cycles
        if (BlockCycles && propagation.Contains(signal))
            return SignalBlockReason.CycleDetected;

        // Check if parent was terminal
        if (TerminalSignals is { Count: > 0 } && propagation.Path.Count > 0)
        {
            var lastSignal = propagation.Path[^1];
            if (TerminalSignals.Contains(lastSignal))
                return SignalBlockReason.TerminalSignalReached;
        }

        return null;
    }

    /// <summary>
    ///     Check if a signal should not propagate (is a leaf).
    ///     Leaf signals are emitted but don't carry propagation context forward.
    /// </summary>
    public bool IsLeaf(string signal)
    {
        return LeafSignals?.Contains(signal) == true;
    }
}

/// <summary>
///     Reason why a signal was blocked.
/// </summary>
public enum SignalBlockReason
{
    /// <summary>
    ///     Signal would exceed maximum propagation depth.
    /// </summary>
    MaxDepthExceeded,

    /// <summary>
    ///     Signal would create a cycle (same signal already in propagation path).
    /// </summary>
    CycleDetected,

    /// <summary>
    ///     Previous signal in chain was marked as terminal.
    /// </summary>
    TerminalSignalReached
}

/// <summary>
///     Minimal interface for emitting signals from within operation bodies.
///     Provides three convenience levels:
///     - Emit() → atom-scoped (default, most specific)
///     - EmitCoordinatorSignal() → coordinator-scoped (all atoms)
///     - EmitSinkSignal() → sink-scoped (all coordinators + atoms)
/// </summary>
public interface ISignalEmitter
{
    /// <summary>
    ///     The operation ID (for correlation).
    /// </summary>
    long OperationId { get; }

    /// <summary>
    ///     The operation key (if any).
    /// </summary>
    string? Key { get; }

    /// <summary>
    ///     Raise an atom-scoped signal on this operation (most specific).
    ///     This is the default, most common case.
    /// </summary>
    void Emit(string signal);

    /// <summary>
    ///     Raise a coordinator-scoped signal (applies to all atoms in this coordinator).
    ///     Use for batch-level or coordinator-wide state: "batch.completed", "throttled".
    /// </summary>
    void EmitCoordinatorSignal(string signal) => Emit(signal); // Default: same as Emit

    /// <summary>
    ///     Raise a sink-scoped signal (applies to entire sink).
    ///     Use for global state: "health.failed", "shutdown".
    /// </summary>
    void EmitSinkSignal(string signal) => Emit(signal); // Default: same as Emit

    /// <summary>
    ///     Raise a signal that was caused by another signal (for propagation tracking).
    ///     Returns false if blocked by constraints.
    /// </summary>
    bool EmitCaused(string signal, SignalPropagation? cause);

    /// <summary>
    ///     Remove a signal from this operation.
    ///     Returns true if the signal was found and removed.
    /// </summary>
    bool Retract(string signal);

    /// <summary>
    ///     Remove all signals matching a pattern from this operation.
    ///     Returns the number of signals removed.
    /// </summary>
    int RetractMatching(string pattern);

    /// <summary>
    ///     Check if this operation has a specific signal.
    /// </summary>
    bool HasSignal(string signal);
}

/// <summary>
///     Signal sink: Shared view over multiple coordinators' operations.
///     Provides live pub/sub (Raise/Subscribe) and aggregated queries (Sense/Detect).
///     Operations own their signals; sink is just a registry and view aggregator.
/// </summary>
public sealed class SignalSink
{
    // Registry of coordinators using this sink (weak references to avoid memory leaks)
    private readonly ConcurrentBag<WeakReference<CoordinatorBase>> _coordinators = new();
    private long _lastCleanupTicks;

    // Lock-free listener array for optimal performance
    private volatile Action<SignalEvent>[] _listeners = Array.Empty<Action<SignalEvent>>();
    private readonly object _listenersLock = new();


    [Obsolete("maxCapacity and maxAge are no longer used. Coordinators manage their own capacity/lifetime.", false)]
    public SignalSink(int maxCapacity = 1000, TimeSpan? maxAge = null)
    {
        // Parameters kept for backward compatibility but ignored
        // Coordinators manage their own MaxTrackedOperations and MaxOperationLifetime
    }

    /// <summary>
    ///     Register a coordinator with this sink for aggregated queries.
    ///     Called automatically when coordinator is created with Signals = this sink.
    /// </summary>
    internal void RegisterCoordinator(CoordinatorBase coordinator)
    {
        _coordinators.Add(new WeakReference<CoordinatorBase>(coordinator));
    }

    /// <summary>
    ///     Get all coordinator IDs registered with this sink.
    /// </summary>
    public IReadOnlyList<string> GetCoordinatorIds()
    {
        var ids = new List<string>();
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord) && coord.Options.CoordinatorId != null)
            {
                ids.Add(coord.Options.CoordinatorId);
            }
        }
        return ids;
    }

    /// <summary>
    ///     Count of coordinators currently registered (approximate).
    /// </summary>
    public int CoordinatorCount
    {
        get
        {
            int count = 0;
            foreach (var coordRef in _coordinators)
                if (coordRef.TryGetTarget(out _))
                    count++;
            return count;
        }
    }

    /// <summary>
    ///     Raised immediately whenever a signal is enqueued. Use for live subscribers that need push semantics.
    /// </summary>
    [Obsolete("Use Subscribe() instead for better performance. This event will be removed in version 2.0.", false)]
    public event Action<SignalEvent>? SignalRaised;

    /// <summary>
    ///     Raise a signal to live subscribers.
    ///     Note: Signals are stored on operations, not in the sink.
    ///     This method only notifies live subscribers for pub/sub patterns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Raise(SignalEvent signal)
    {
        // Lock-free listener invocation - volatile read ensures we get latest array
        var listeners = _listeners;
        var len = listeners.Length;

        // Manual loop for better inlining and branch prediction
        for (int i = 0; i < len; i++)
        {
            try
            {
                listeners[i](signal);
            }
            catch
            {
                /* never throw from signal fan-out */
            }
        }

        // Legacy event support - kept for backward compatibility
        #pragma warning disable CS0618 // Type or member is obsolete
        var handler = SignalRaised;
        #pragma warning restore CS0618
        if (handler is not null)
        {
            try
            {
                handler(signal);
            }
            catch
            {
                /* never throw from signal fan-out */
            }
        }
    }

    /// <summary>
    ///     Raise a signal with just the name (generates ID and timestamp).
    /// </summary>
    public void Raise(string signal, string? key = null)
    {
        Raise(new SignalEvent(signal, EphemeralIdGenerator.NextId(), key, DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Sense all signals from all registered coordinators.
    ///     Returns aggregated view of operations' signals across all coordinators.
    /// </summary>
    public IReadOnlyList<SignalEvent> Sense()
    {
        var results = new List<SignalEvent>();
        MaybeCleanupDeadCoordinators();

        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                results.AddRange(coord.GetSignals());
            }
        }
        return results;
    }

    /// <summary>
    ///     Sense signals matching predicate across all coordinators.
    /// </summary>
    public IReadOnlyList<SignalEvent> Sense(Func<SignalEvent, bool> predicate)
    {
        var results = new List<SignalEvent>();
        MaybeCleanupDeadCoordinators();

        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                foreach (var signal in coord.GetSignals())
                {
                    if (predicate(signal))
                        results.Add(signal);
                }
            }
        }
        return results;
    }

    /// <summary>
    ///     Sense signals from a specific coordinator ID.
    /// </summary>
    public IReadOnlyList<SignalEvent> SenseByCoordinator(string coordinatorId)
    {
        var results = new List<SignalEvent>();
        MaybeCleanupDeadCoordinators();

        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord) &&
                coord.Options.CoordinatorId == coordinatorId)
            {
                results.AddRange(coord.GetSignals());
            }
        }
        return results;
    }

    /// <summary>
    ///     Sense signals from coordinators matching a pattern.
    /// </summary>
    public IReadOnlyList<SignalEvent> SenseByCoordinatorPattern(string pattern)
    {
        var results = new List<SignalEvent>();
        MaybeCleanupDeadCoordinators();

        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord) &&
                coord.Options.CoordinatorId != null &&
                StringPatternMatcher.Matches(coord.Options.CoordinatorId, pattern))
            {
                results.AddRange(coord.GetSignals());
            }
        }
        return results;
    }

    /// <summary>
    ///     Get signals for a specific operation ID across all coordinators.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IReadOnlyList<SignalEvent> GetOpSignals(long operationId)
    {
        var results = new List<SignalEvent>();
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                foreach (var signal in coord.GetSignals())
                {
                    if (signal.OperationId == operationId)
                        results.Add(signal);
                }
            }
        }
        return results;
    }

    /// <summary>
    ///     Get signals for a specific operation ID matching a pattern.
    ///     Convenience method for: Sense(s => s.OperationId == operationId && matches(pattern))
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IReadOnlyList<SignalEvent> GetOpSignals(long operationId, string pattern)
    {
        return Sense(s => s.OperationId == operationId && StringPatternMatcher.Matches(s.Signal, pattern));
    }

    /// <summary>
    ///     Get operation summary from signals. Returns null if no signals found for this operation.
    ///     Convenience method that aggregates signal history for an operation.
    /// </summary>
    public OperationSignalSummary? GetOp(long operationId)
    {
        var signals = GetOpSignals(operationId);
        if (signals.Count == 0)
            return null;

        return new OperationSignalSummary(
            operationId,
            signals[0].Key,
            signals[0].Timestamp,
            signals[^1].Timestamp,
            signals);
    }

    /// <summary>
    ///     Detect any signals matching predicate across all coordinators.
    ///     Short-circuits on first match.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Detect(Func<SignalEvent, bool> predicate)
    {
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                foreach (var signal in coord.GetSignals())
                {
                    if (predicate(signal))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    ///     Detect any signals with name across all coordinators.
    ///     Short-circuits on first match - delegates to coordinator HasSignal() for performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Detect(string signalName)
    {
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                if (coord.HasSignal(signalName))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Detect any signals with name by coordinator ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool DetectByCoordinator(string coordinatorId, string signalName)
    {
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord) &&
                coord.Options.CoordinatorId == coordinatorId)
            {
                return coord.HasSignal(signalName);
            }
        }
        return false;
    }

    /// <summary>
    ///     Detect any signals with name (span overload - delegates to string version).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Detect(ReadOnlySpan<char> signalName)
    {
        return Detect(signalName.ToString());
    }

    /// <summary>
    ///     Count signals matching a prefix pattern within a time window.
    ///     Optimized for health-check pattern: count recent failures.
    ///     Uses StartsWith instead of Contains for better performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountRecentByPrefix(string prefix, DateTimeOffset since)
    {
        int count = 0;
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                foreach (var s in coord.GetSignals())
                {
                    if (s.Timestamp >= since && s.Signal.StartsWith(prefix, StringComparison.Ordinal))
                        count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    ///     Count signals containing a substring within a time window.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountRecentByContains(string substring, DateTimeOffset since)
    {
        int count = 0;
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                foreach (var s in coord.GetSignals())
                {
                    if (s.Timestamp >= since && s.Signal.Contains(substring))
                        count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    ///     Count exact signal matches within a time window.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountRecentExact(string signalName, DateTimeOffset since)
    {
        int count = 0;
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out var coord))
            {
                foreach (var s in coord.GetSignals())
                {
                    if (s.Timestamp >= since && string.Equals(s.Signal, signalName, StringComparison.Ordinal))
                        count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    ///     Subscribe to signal events with optimal lock-free performance.
    ///     Preferred over SignalRaised event.
    /// </summary>
    /// <param name="listener">Callback to invoke for each signal.</param>
    /// <returns>IDisposable that unsubscribes when disposed.</returns>
    public IDisposable Subscribe(Action<SignalEvent> listener)
    {
        if (listener == null) throw new ArgumentNullException(nameof(listener));

        lock (_listenersLock)
        {
            var current = _listeners;
            var newListeners = new Action<SignalEvent>[current.Length + 1];
            Array.Copy(current, newListeners, current.Length);
            newListeners[current.Length] = listener;
            _listeners = newListeners;
        }

        return new Subscription(this, listener);
    }

    internal void Unsubscribe(Action<SignalEvent> listener)
    {
        lock (_listenersLock)
        {
            var current = _listeners;
            var index = Array.IndexOf(current, listener);
            if (index < 0) return;

            var newListeners = new Action<SignalEvent>[current.Length - 1];
            Array.Copy(current, 0, newListeners, 0, index);
            Array.Copy(current, index + 1, newListeners, index, current.Length - index - 1);
            _listeners = newListeners;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly SignalSink _sink;
        private readonly Action<SignalEvent> _listener;
        private int _disposed;

        public Subscription(SignalSink sink, Action<SignalEvent> listener)
        {
            _sink = sink;
            _listener = listener;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _sink.Unsubscribe(_listener);
            }
        }
    }

    /// <summary>
    ///     Periodically clean up dead coordinator references.
    ///     Called automatically during queries; no manual cleanup needed.
    /// </summary>
    private void MaybeCleanupDeadCoordinators()
    {
        var now = Environment.TickCount64;
        var lastCleanup = Volatile.Read(ref _lastCleanupTicks);

        // Cleanup every ~5 seconds
        if (now - lastCleanup < 5000)
            return;

        if (Interlocked.CompareExchange(ref _lastCleanupTicks, now, lastCleanup) != lastCleanup)
            return; // Another thread is cleaning up

        // Remove dead references
        var alive = new ConcurrentBag<WeakReference<CoordinatorBase>>();
        foreach (var coordRef in _coordinators)
        {
            if (coordRef.TryGetTarget(out _))
                alive.Add(coordRef);
        }

        // Replace with alive-only bag (ConcurrentBag doesn't support removal)
        // This is rare (only on coordinator disposal) so allocation is acceptable
        if (alive.Count < _coordinators.Count)
        {
            _coordinators.Clear();
            foreach (var coordRef in alive)
                _coordinators.Add(coordRef);
        }
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    ///     Operations own their signals; use coordinator methods to manage signal lifetime.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int Clear()
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int ClearMatching(Func<SignalEvent, bool> predicate)
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int ClearPattern(string pattern)
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int ClearOperation(long operationId)
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int ClearKey(string key)
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     Request all coordinators attached to this sink to begin draining.
    ///     Emits "coordinator.drain.all" signal that coordinators listen for.
    ///     Coordinators with DrainOnSignals matching this pattern will complete intake and drain.
    /// </summary>
    public void RequestDrainAll()
    {
        Raise(new SignalEvent(
            "coordinator.drain.all",
            EphemeralIdGenerator.NextId(),
            null,
            DateTimeOffset.UtcNow
        ));
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int ClearOlderThan(TimeSpan olderThan)
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     [Obsolete] Clear operations don't apply to sink-as-view model.
    /// </summary>
    [Obsolete("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.", false)]
    public int ClearOldest(int count)
    {
        throw new NotSupportedException("Sink no longer stores signals. Operations own signals; coordinators manage lifetime.");
    }

    /// <summary>
    ///     Request drain of all coordinators.
    ///     Emits "coordinator.drain.all" signal that coordinators listen for.
    /// </summary>
    public void RequestDrainAndClear()
    {
        RequestDrainAll();
    }

    /// <summary>
    ///     Request drain of a specific coordinator by ID.
    ///     Emits "coordinator.drain.id:{id}" signal.
    /// </summary>
    /// <param name="coordinatorId">The coordinator ID to drain (matches coordinator's OperationId or configured ID).</param>
    public void RequestDrain(long coordinatorId)
    {
        Raise(new SignalEvent(
            "coordinator.drain.id",
            EphemeralIdGenerator.NextId(),
            coordinatorId.ToString(),
            DateTimeOffset.UtcNow
        ));
    }

    /// <summary>
    ///     Request drain of all coordinators matching a specific pattern.
    ///     Emits "coordinator.drain.pattern" signal with the pattern in the Key field.
    ///     Coordinators can match against their name, key, or other identifiers.
    /// </summary>
    /// <param name="pattern">Pattern to match coordinator identifiers (glob-style with * and ?).</param>
    public void RequestDrainPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) throw new ArgumentNullException(nameof(pattern));

        Raise(new SignalEvent(
            "coordinator.drain.pattern",
            EphemeralIdGenerator.NextId(),
            pattern,
            DateTimeOffset.UtcNow
        ));
    }

}


/// <summary>
///     Processes signals asynchronously in a bounded background queue.
///     Non-blocking: signal emission returns immediately, processing happens in background.
/// </summary>
public sealed class AsyncSignalProcessor : IAsyncDisposable
{
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<SignalEvent, CancellationToken, Task> _handler;
    private readonly int _maxQueueSize;
    private readonly Task _processorTask;
    private readonly ConcurrentQueue<SignalEvent> _queue = new();
    private long _droppedCount;
    private long _processedCount;
    private int _queuedCount;

    /// <summary>
    ///     Creates a new async signal processor.
    /// </summary>
    /// <param name="handler">Async handler for each signal.</param>
    /// <param name="maxConcurrency">Max concurrent handlers (default: 4).</param>
    /// <param name="maxQueueSize">Max queued signals before dropping (default: 1000).</param>
    public AsyncSignalProcessor(
        Func<SignalEvent, CancellationToken, Task> handler,
        int maxConcurrency = 4,
        int maxQueueSize = 1000)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _maxQueueSize = maxQueueSize;
        _processorTask = ProcessQueueAsync();
    }

    /// <summary>
    ///     Number of signals currently queued.
    /// </summary>
    public int QueuedCount => Volatile.Read(ref _queuedCount);

    /// <summary>
    ///     Total signals successfully processed.
    /// </summary>
    public long ProcessedCount => Volatile.Read(ref _processedCount);

    /// <summary>
    ///     Total signals dropped due to queue overflow.
    /// </summary>
    public long DroppedCount => Volatile.Read(ref _droppedCount);

    /// <summary>
    ///     Stops the processor and waits for pending work to complete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        // Wait for processor to finish with timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _processorTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout - processor didn't finish gracefully
        }

        _concurrencyGate.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    ///     Enqueue a signal for async processing.
    ///     Returns immediately. Signal will be processed in background.
    /// </summary>
    /// <returns>True if enqueued, false if dropped due to queue overflow.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Enqueue(SignalEvent signal)
    {
        // Fast path: check queue size without lock
        if (Volatile.Read(ref _queuedCount) >= _maxQueueSize)
        {
            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        _queue.Enqueue(signal);
        Interlocked.Increment(ref _queuedCount);
        return true;
    }

    private async Task ProcessQueueAsync()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            // Wait for work or cancellation
            while (_queue.IsEmpty && !token.IsCancellationRequested)
            {
#if NET8_0_OR_GREATER
                await Task.Delay(10, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#else
                try { await Task.Delay(10, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* Suppressed */ }
#endif
            }

            if (token.IsCancellationRequested)
                break;

            // Process available signals
            while (_queue.TryDequeue(out var signal))
            {
                Interlocked.Decrement(ref _queuedCount);

                // Acquire concurrency slot
                await _concurrencyGate.WaitAsync(token).ConfigureAwait(false);

                // Fire and forget with slot release
                _ = ProcessSignalAsync(signal, token);
            }
        }
    }

    private async Task ProcessSignalAsync(SignalEvent signal, CancellationToken token)
    {
        try
        {
            await _handler(signal, token).ConfigureAwait(false);
            Interlocked.Increment(ref _processedCount);
        }
        catch
        {
            // Swallow exceptions - async handlers should handle their own errors
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }
}

/// <summary>
/// Summary of an operation derived from its signal history.
/// Provides a lightweight view of operation lifecycle without accessing coordinator state.
/// </summary>
public sealed record OperationSignalSummary(
    long OperationId,
    string? Key,
    DateTimeOffset FirstSignalTime,
    DateTimeOffset LastSignalTime,
    IReadOnlyList<SignalEvent> Signals)
{
    /// <summary>
    /// Duration between first and last signal.
    /// </summary>
    public TimeSpan Duration => LastSignalTime - FirstSignalTime;

    /// <summary>
    /// Total number of signals emitted by this operation.
    /// </summary>
    public int SignalCount => Signals.Count;

    /// <summary>
    /// Check if operation has a specific signal.
    /// </summary>
    public bool HasSignal(string signal) => Signals.Any(s => s.Signal == signal);

    /// <summary>
    /// Check if operation has signals matching a pattern.
    /// </summary>
    public bool HasSignalPattern(string pattern) =>
        Signals.Any(s => StringPatternMatcher.Matches(s.Signal, pattern));

    /// <summary>
    /// Get all signals matching a pattern.
    /// </summary>
    public IEnumerable<SignalEvent> GetSignals(string pattern) =>
        Signals.Where(s => StringPatternMatcher.Matches(s.Signal, pattern));
}
