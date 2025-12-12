using System.Runtime.CompilerServices;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Operation tracking for non-result-returning work.
///     Exposed publicly to enable signal emission with correct operation IDs in parallel work.
/// </summary>
public sealed class EphemeralOperation : IEphemeralOperationCore
{
    private readonly SignalConstraints? _constraints;
    private readonly Action<SignalEvent>? _onSignal;
    private readonly Action<SignalRetractedEvent>? _onSignalRetracted;
    private readonly SignalSink? _sink;
    internal List<SignalEvent>? _signals;
    private Dictionary<string, object>? _state;

    public EphemeralOperation(
        SignalSink? sink = null,
        Action<SignalEvent>? onSignal = null,
        Action<SignalRetractedEvent>? onSignalRetracted = null,
        SignalConstraints? constraints = null,
        long? id = null)
    {
        _sink = sink;
        _onSignal = onSignal;
        _onSignalRetracted = onSignalRetracted;
        _constraints = constraints;
        Id = id ?? EphemeralIdGenerator.NextId();
    }

    public long Id { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Completed { get; set; }
    public Exception? Error { get; set; }
    public string? Key { get; init; }

    /// <summary>
    ///     When true, this operation survives time and size evictions.
    ///     Use for long-lived status/sentinel operations.
    /// </summary>
    public bool IsPinned { get; set; }

    public TimeSpan? Duration =>
        Completed is { } done ? done - Started : null;

    // ISignalEmitter
    long ISignalEmitter.OperationId => Id;
    string? ISignalEmitter.Key => Key;

    // ISignalEmitter implementation
    void ISignalEmitter.Emit(string signal)
    {
        Signal(signal);
    }

    bool ISignalEmitter.EmitCaused(string signal, SignalPropagation? cause)
    {
        return EmitCaused(signal, cause);
    }

    bool ISignalEmitter.Retract(string signal)
    {
        return Retract(signal);
    }

    int ISignalEmitter.RetractMatching(string pattern)
    {
        return RetractMatching(pattern);
    }

    bool ISignalEmitter.HasSignal(string signal)
    {
        return HasSignal(signal);
    }

    /// <summary>
    ///     Raise a signal on this operation.
    /// </summary>
    public void Signal(string signal)
    {
        EmitCausedInternal(signal, null);
    }

    /// <summary>
    ///     Raise a signal caused by another signal (for propagation tracking).
    ///     Returns false if blocked by constraints.
    /// </summary>
    public bool EmitCaused(string signal, SignalPropagation? cause)
    {
        return EmitCausedInternal(signal, cause);
    }

    private bool EmitCausedInternal(string signal, SignalPropagation? cause)
    {
        // Check constraints if we have them
        if (_constraints is not null)
        {
            var blockReason = _constraints.ShouldBlock(signal, cause);
            if (blockReason is not null)
            {
                var blockedEvt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, cause);
                try
                {
                    _constraints.OnBlocked?.Invoke(blockedEvt, blockReason.Value);
                }
                catch
                {
                    /* Don't propagate callback exceptions */
                }

                return false;
            }
        }

        // Build propagation chain: extend from cause, or start new if leaf/null
        SignalPropagation? propagation = null;
        if (_constraints?.IsLeaf(signal) != true) propagation = cause?.Extend(signal) ?? SignalPropagation.Root(signal);

        var evt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, propagation);

        // Store full event on operation (single source of truth)
        _signals ??= new List<SignalEvent>();
        _signals.Add(evt);

        // Notify sink for live subscribers (sink doesn't own storage)
        _sink?.Raise(evt);

        if (_onSignal is not null)
            try
            {
                _onSignal(evt);
            }
            catch
            {
                /* Don't propagate callback exceptions */
            }

        return true;
    }

    /// <summary>
    ///     Remove a signal from this operation.
    ///     Returns true if the signal was found and removed.
    /// </summary>
    public bool Retract(string signal)
    {
        if (_signals is null) return false;

        var removed = false;
        for (int i = _signals.Count - 1; i >= 0; i--)
        {
            if (_signals[i].Signal == signal)
            {
                _signals.RemoveAt(i);
                removed = true;
                break; // Remove first match only
            }
        }

        if (removed)
            NotifyRetracted(signal, false, null);

        return removed;
    }

    /// <summary>
    ///     Remove all signals matching a pattern from this operation.
    ///     Returns the number of signals removed.
    /// </summary>
    public int RetractMatching(string pattern)
    {
        if (_signals is null) return 0;

        var removed = new List<string>();
        _signals.RemoveAll(evt =>
        {
            if (StringPatternMatcher.Matches(evt.Signal, pattern))
            {
                removed.Add(evt.Signal);
                return true;
            }

            return false;
        });

        foreach (var signal in removed) NotifyRetracted(signal, true, pattern);

        return removed.Count;
    }

    private void NotifyRetracted(string signal, bool wasPatternMatch, string? pattern)
    {
        if (_onSignalRetracted is null) return;

        var evt = new SignalRetractedEvent(signal, Id, Key, DateTimeOffset.UtcNow, wasPatternMatch, pattern);
        try
        {
            _onSignalRetracted(evt);
        }
        catch
        {
            /* Don't propagate callback exceptions */
        }
    }

    /// <summary>
    ///     Check if this operation has a specific signal.
    /// </summary>
    public bool HasSignal(string signal)
    {
        if (_signals is null) return false;
        foreach (var evt in _signals)
            if (evt.Signal == signal)
                return true;
        return false;
    }

    /// <summary>
    ///     Gets all signals from this operation.
    /// </summary>
    /// <returns>Read-only list of signal events.</returns>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return _signals ?? (IReadOnlyList<SignalEvent>)Array.Empty<SignalEvent>();
    }

    /// <summary>
    ///     Remove signals older than the specified age from this operation.
    ///     Operations manage their own signals - no coordinator involvement needed.
    /// </summary>
    /// <param name="olderThan">Remove signals older than this timespan from now.</param>
    /// <returns>Number of signals removed.</returns>
    public int CleanupSignals(TimeSpan olderThan)
    {
        if (_signals is null) return 0;
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        return _signals.RemoveAll(s => s.Timestamp < cutoff);
    }

    /// <summary>
    ///     Remove the oldest signals, keeping only the most recent N.
    ///     Operations manage their own signals - no coordinator involvement needed.
    /// </summary>
    /// <param name="keepCount">Number of most recent signals to keep.</param>
    /// <returns>Number of signals removed.</returns>
    public int CleanupSignals(int keepCount)
    {
        if (_signals is null || _signals.Count <= keepCount) return 0;
        var toRemove = _signals.Count - keepCount;
        _signals.RemoveRange(0, toRemove);
        return toRemove;
    }

    /// <summary>
    ///     Remove signals matching the specified pattern from this operation.
    ///     Operations manage their own signals - no coordinator involvement needed.
    /// </summary>
    /// <param name="pattern">Pattern to match signal names (e.g., "error.*", "api.*.timeout").</param>
    /// <returns>Number of signals removed.</returns>
    public int CleanupSignals(string pattern)
    {
        if (_signals is null) return 0;
        return _signals.RemoveAll(evt => StringPatternMatcher.Matches(evt.Signal, pattern));
    }

    /// <summary>
    ///     Set a state value for this operation. All operations have state storage available.
    /// </summary>
    public void SetState(string key, object value)
    {
        _state ??= new Dictionary<string, object>();
        _state[key] = value;
    }

    /// <summary>
    ///     Get a state value from this operation, or null if not found.
    /// </summary>
    public object? GetState(string key)
    {
        if (_state is null) return null;
        return _state.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    ///     Get a strongly-typed state value, or default if not found or wrong type.
    /// </summary>
    public T? GetState<T>(string key)
    {
        var value = GetState(key);
        return value is T typed ? typed : default;
    }

    /// <summary>
    ///     Check if a state key exists.
    /// </summary>
    public bool HasState(string key)
    {
        return _state?.ContainsKey(key) ?? false;
    }

    public EphemeralOperationSnapshot ToSnapshot()
    {
        return new EphemeralOperationSnapshot(Id, Started, Completed, Key, Error != null, Error, Duration, _signals,
            IsPinned);
    }
}

/// <summary>
///     Operation tracking for result-returning work.
/// </summary>
public sealed class EphemeralOperation<TResult> : IEphemeralOperationCore
{
    private readonly SignalConstraints? _constraints;
    private readonly Action<SignalEvent>? _onSignal;
    private readonly Action<SignalRetractedEvent>? _onSignalRetracted;
    private readonly SignalSink? _sink;
    internal List<SignalEvent>? _signals;
    private Dictionary<string, object>? _state;

    public EphemeralOperation(
        SignalSink? sink = null,
        Action<SignalEvent>? onSignal = null,
        Action<SignalRetractedEvent>? onSignalRetracted = null,
        SignalConstraints? constraints = null,
        long? id = null)
    {
        _sink = sink;
        _onSignal = onSignal;
        _onSignalRetracted = onSignalRetracted;
        _constraints = constraints;
        Id = id ?? EphemeralIdGenerator.NextId();
    }

    public long Id { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Completed { get; set; }
    public Exception? Error { get; set; }
    public string? Key { get; init; }
    public TResult? Result { get; set; }
    public bool HasResult { get; set; }

    /// <summary>
    ///     When true, this operation survives time and size evictions.
    ///     Use for long-lived status/sentinel operations.
    /// </summary>
    public bool IsPinned { get; set; }

    public TimeSpan? Duration =>
        Completed is { } done ? done - Started : null;

    public bool IsSuccess => Completed.HasValue && Error is null;

    // ISignalEmitter
    long ISignalEmitter.OperationId => Id;
    string? ISignalEmitter.Key => Key;

    // ISignalEmitter implementation
    void ISignalEmitter.Emit(string signal)
    {
        Signal(signal);
    }

    bool ISignalEmitter.EmitCaused(string signal, SignalPropagation? cause)
    {
        return EmitCaused(signal, cause);
    }

    bool ISignalEmitter.Retract(string signal)
    {
        return Retract(signal);
    }

    int ISignalEmitter.RetractMatching(string pattern)
    {
        return RetractMatching(pattern);
    }

    bool ISignalEmitter.HasSignal(string signal)
    {
        return HasSignal(signal);
    }

    /// <summary>
    ///     Raise a signal on this operation.
    /// </summary>
    public void Signal(string signal)
    {
        EmitCausedInternal(signal, null);
    }

    /// <summary>
    ///     Raise a signal caused by another signal (for propagation tracking).
    ///     Returns false if blocked by constraints.
    /// </summary>
    public bool EmitCaused(string signal, SignalPropagation? cause)
    {
        return EmitCausedInternal(signal, cause);
    }

    private bool EmitCausedInternal(string signal, SignalPropagation? cause)
    {
        // Check constraints if we have them
        if (_constraints is not null)
        {
            var blockReason = _constraints.ShouldBlock(signal, cause);
            if (blockReason is not null)
            {
                var blockedEvt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, cause);
                try
                {
                    _constraints.OnBlocked?.Invoke(blockedEvt, blockReason.Value);
                }
                catch
                {
                    /* Don't propagate callback exceptions */
                }

                return false;
            }
        }

        // Build propagation chain: extend from cause, or start new if leaf/null
        SignalPropagation? propagation = null;
        if (_constraints?.IsLeaf(signal) != true) propagation = cause?.Extend(signal) ?? SignalPropagation.Root(signal);

        var evt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, propagation);

        // Store full event on operation (single source of truth)
        _signals ??= new List<SignalEvent>();
        _signals.Add(evt);

        // Notify sink for live subscribers (sink doesn't own storage)
        _sink?.Raise(evt);

        if (_onSignal is not null)
            try
            {
                _onSignal(evt);
            }
            catch
            {
                /* Don't propagate callback exceptions */
            }

        return true;
    }

    /// <summary>
    ///     Remove a signal from this operation.
    ///     Returns true if the signal was found and removed.
    /// </summary>
    public bool Retract(string signal)
    {
        if (_signals is null) return false;

        var removed = false;
        for (int i = _signals.Count - 1; i >= 0; i--)
        {
            if (_signals[i].Signal == signal)
            {
                _signals.RemoveAt(i);
                removed = true;
                break; // Remove first match only
            }
        }

        if (removed)
            NotifyRetracted(signal, false, null);

        return removed;
    }

    /// <summary>
    ///     Remove all signals matching a pattern from this operation.
    ///     Returns the number of signals removed.
    /// </summary>
    public int RetractMatching(string pattern)
    {
        if (_signals is null) return 0;

        var removed = new List<string>();
        _signals.RemoveAll(evt =>
        {
            if (StringPatternMatcher.Matches(evt.Signal, pattern))
            {
                removed.Add(evt.Signal);
                return true;
            }

            return false;
        });

        foreach (var signal in removed) NotifyRetracted(signal, true, pattern);

        return removed.Count;
    }

    private void NotifyRetracted(string signal, bool wasPatternMatch, string? pattern)
    {
        if (_onSignalRetracted is null) return;

        var evt = new SignalRetractedEvent(signal, Id, Key, DateTimeOffset.UtcNow, wasPatternMatch, pattern);
        try
        {
            _onSignalRetracted(evt);
        }
        catch
        {
            /* Don't propagate callback exceptions */
        }
    }

    /// <summary>
    ///     Check if this operation has a specific signal.
    /// </summary>
    public bool HasSignal(string signal)
    {
        if (_signals is null) return false;
        foreach (var evt in _signals)
            if (evt.Signal == signal)
                return true;
        return false;
    }

    /// <summary>
    ///     Gets all signals from this operation.
    /// </summary>
    /// <returns>Read-only list of signal events.</returns>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return _signals ?? (IReadOnlyList<SignalEvent>)Array.Empty<SignalEvent>();
    }

    /// <summary>
    ///     Remove signals older than the specified age from this operation.
    ///     Operations manage their own signals - no coordinator involvement needed.
    /// </summary>
    /// <param name="olderThan">Remove signals older than this timespan from now.</param>
    /// <returns>Number of signals removed.</returns>
    public int CleanupSignals(TimeSpan olderThan)
    {
        if (_signals is null) return 0;
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        return _signals.RemoveAll(s => s.Timestamp < cutoff);
    }

    /// <summary>
    ///     Remove the oldest signals, keeping only the most recent N.
    ///     Operations manage their own signals - no coordinator involvement needed.
    /// </summary>
    /// <param name="keepCount">Number of most recent signals to keep.</param>
    /// <returns>Number of signals removed.</returns>
    public int CleanupSignals(int keepCount)
    {
        if (_signals is null || _signals.Count <= keepCount) return 0;
        var toRemove = _signals.Count - keepCount;
        _signals.RemoveRange(0, toRemove);
        return toRemove;
    }

    /// <summary>
    ///     Remove signals matching the specified pattern from this operation.
    ///     Operations manage their own signals - no coordinator involvement needed.
    /// </summary>
    /// <param name="pattern">Pattern to match signal names (e.g., "error.*", "api.*.timeout").</param>
    /// <returns>Number of signals removed.</returns>
    public int CleanupSignals(string pattern)
    {
        if (_signals is null) return 0;
        return _signals.RemoveAll(evt => StringPatternMatcher.Matches(evt.Signal, pattern));
    }

    /// <summary>
    ///     Set a state value for this operation. All operations have state storage available.
    /// </summary>
    public void SetState(string key, object value)
    {
        _state ??= new Dictionary<string, object>();
        _state[key] = value;
    }

    /// <summary>
    ///     Get a state value from this operation, or null if not found.
    /// </summary>
    public object? GetState(string key)
    {
        if (_state is null) return null;
        return _state.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    ///     Get a strongly-typed state value, or default if not found or wrong type.
    /// </summary>
    public T? GetState<T>(string key)
    {
        var value = GetState(key);
        return value is T typed ? typed : default;
    }

    /// <summary>
    ///     Check if a state key exists.
    /// </summary>
    public bool HasState(string key)
    {
        return _state?.ContainsKey(key) ?? false;
    }

    public EphemeralOperationSnapshot<TResult> ToSnapshot()
    {
        return new EphemeralOperationSnapshot<TResult>(Id, Started, Completed, Key, Error != null, Error, Duration,
            Result, HasResult, _signals,
            IsPinned);
    }

    public EphemeralOperationSnapshot ToBaseSnapshot()
    {
        return new EphemeralOperationSnapshot(Id, Started, Completed, Key, Error != null, Error, Duration, _signals,
            IsPinned);
    }

    // Explicit interface implementation for IEphemeralOperationCore
    EphemeralOperationSnapshot IEphemeralOperationCore.ToSnapshot() => ToBaseSnapshot();
}