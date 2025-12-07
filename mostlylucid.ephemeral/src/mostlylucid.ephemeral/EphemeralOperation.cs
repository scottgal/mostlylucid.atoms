namespace Mostlylucid.Ephemeral;

/// <summary>
/// Internal operation tracking for non-result-returning work.
/// </summary>
internal sealed class EphemeralOperation : ISignalEmitter
{
    private readonly SignalSink? _sink;
    private readonly Action<SignalEvent>? _onSignal;
    private readonly Action<SignalRetractedEvent>? _onSignalRetracted;
    private readonly SignalConstraints? _constraints;
    internal List<string>? _signals;

    public long Id { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Completed { get; set; }
    public Exception? Error { get; set; }
    public string? Key { get; init; }

    /// <summary>
    /// When true, this operation survives time and size evictions.
    /// Use for long-lived status/sentinel operations.
    /// </summary>
    public bool IsPinned { get; set; }

    // ISignalEmitter
    long ISignalEmitter.OperationId => Id;
    string? ISignalEmitter.Key => Key;

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

    public TimeSpan? Duration =>
        Completed is { } done ? done - Started : null;

    /// <summary>
    /// Raise a signal on this operation.
    /// </summary>
    public void Signal(string signal) => EmitCausedInternal(signal, null);

    /// <summary>
    /// Raise a signal caused by another signal (for propagation tracking).
    /// Returns false if blocked by constraints.
    /// </summary>
    public bool EmitCaused(string signal, SignalPropagation? cause)
        => EmitCausedInternal(signal, cause);

    private bool EmitCausedInternal(string signal, SignalPropagation? cause)
    {
        // Check constraints if we have them
        if (_constraints is not null)
        {
            var blockReason = _constraints.ShouldBlock(signal, cause);
            if (blockReason is not null)
            {
                var blockedEvt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, cause);
                try { _constraints.OnBlocked?.Invoke(blockedEvt, blockReason.Value); }
                catch { /* Don't propagate callback exceptions */ }
                return false;
            }
        }

        _signals ??= new List<string>();
        _signals.Add(signal);

        // Build propagation chain: extend from cause, or start new if leaf/null
        SignalPropagation? propagation = null;
        if (_constraints?.IsLeaf(signal) != true)
        {
            propagation = cause?.Extend(signal) ?? SignalPropagation.Root(signal);
        }

        var evt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, propagation);
        _sink?.Raise(evt);

        if (_onSignal is not null)
        {
            try { _onSignal(evt); }
            catch { /* Don't propagate callback exceptions */ }
        }

        return true;
    }

    /// <summary>
    /// Remove a signal from this operation.
    /// Returns true if the signal was found and removed.
    /// </summary>
    public bool Retract(string signal)
    {
        if (_signals is null) return false;
        if (!_signals.Remove(signal)) return false;

        NotifyRetracted(signal, wasPatternMatch: false, pattern: null);
        return true;
    }

    /// <summary>
    /// Remove all signals matching a pattern from this operation.
    /// Returns the number of signals removed.
    /// </summary>
    public int RetractMatching(string pattern)
    {
        if (_signals is null) return 0;

        var removed = new List<string>();
        _signals.RemoveAll(s =>
        {
            if (StringPatternMatcher.Matches(s, pattern))
            {
                removed.Add(s);
                return true;
            }
            return false;
        });

        foreach (var signal in removed)
        {
            NotifyRetracted(signal, wasPatternMatch: true, pattern: pattern);
        }

        return removed.Count;
    }

    private void NotifyRetracted(string signal, bool wasPatternMatch, string? pattern)
    {
        if (_onSignalRetracted is null) return;

        var evt = new SignalRetractedEvent(signal, Id, Key, DateTimeOffset.UtcNow, wasPatternMatch, pattern);
        try { _onSignalRetracted(evt); }
        catch { /* Don't propagate callback exceptions */ }
    }

    /// <summary>
    /// Check if this operation has a specific signal.
    /// </summary>
    public bool HasSignal(string signal) => _signals?.Contains(signal) == true;

    // ISignalEmitter implementation
    void ISignalEmitter.Emit(string signal) => Signal(signal);
    bool ISignalEmitter.EmitCaused(string signal, SignalPropagation? cause) => EmitCaused(signal, cause);
    bool ISignalEmitter.Retract(string signal) => Retract(signal);
    int ISignalEmitter.RetractMatching(string pattern) => RetractMatching(pattern);
    bool ISignalEmitter.HasSignal(string signal) => HasSignal(signal);

    public EphemeralOperationSnapshot ToSnapshot() =>
        new(Id, Started, Completed, Key, IsFaulted: Error != null, Error, Duration, _signals, IsPinned);
}

/// <summary>
/// Internal operation tracking for result-returning work.
/// </summary>
internal sealed class EphemeralOperation<TResult> : ISignalEmitter
{
    private readonly SignalSink? _sink;
    private readonly Action<SignalEvent>? _onSignal;
    private readonly Action<SignalRetractedEvent>? _onSignalRetracted;
    private readonly SignalConstraints? _constraints;
    internal List<string>? _signals;

    public long Id { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Completed { get; set; }
    public Exception? Error { get; set; }
    public string? Key { get; init; }
    public TResult? Result { get; set; }
    public bool HasResult { get; set; }

    /// <summary>
    /// When true, this operation survives time and size evictions.
    /// Use for long-lived status/sentinel operations.
    /// </summary>
    public bool IsPinned { get; set; }

    // ISignalEmitter
    long ISignalEmitter.OperationId => Id;
    string? ISignalEmitter.Key => Key;

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

    public TimeSpan? Duration =>
        Completed is { } done ? done - Started : null;

    public bool IsSuccess => Completed.HasValue && Error is null;

    /// <summary>
    /// Raise a signal on this operation.
    /// </summary>
    public void Signal(string signal) => EmitCausedInternal(signal, null);

    /// <summary>
    /// Raise a signal caused by another signal (for propagation tracking).
    /// Returns false if blocked by constraints.
    /// </summary>
    public bool EmitCaused(string signal, SignalPropagation? cause)
        => EmitCausedInternal(signal, cause);

    private bool EmitCausedInternal(string signal, SignalPropagation? cause)
    {
        // Check constraints if we have them
        if (_constraints is not null)
        {
            var blockReason = _constraints.ShouldBlock(signal, cause);
            if (blockReason is not null)
            {
                var blockedEvt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, cause);
                try { _constraints.OnBlocked?.Invoke(blockedEvt, blockReason.Value); }
                catch { /* Don't propagate callback exceptions */ }
                return false;
            }
        }

        _signals ??= new List<string>();
        _signals.Add(signal);

        // Build propagation chain: extend from cause, or start new if leaf/null
        SignalPropagation? propagation = null;
        if (_constraints?.IsLeaf(signal) != true)
        {
            propagation = cause?.Extend(signal) ?? SignalPropagation.Root(signal);
        }

        var evt = new SignalEvent(signal, Id, Key, DateTimeOffset.UtcNow, propagation);
        _sink?.Raise(evt);

        if (_onSignal is not null)
        {
            try { _onSignal(evt); }
            catch { /* Don't propagate callback exceptions */ }
        }

        return true;
    }

    /// <summary>
    /// Remove a signal from this operation.
    /// Returns true if the signal was found and removed.
    /// </summary>
    public bool Retract(string signal)
    {
        if (_signals is null) return false;
        if (!_signals.Remove(signal)) return false;

        NotifyRetracted(signal, wasPatternMatch: false, pattern: null);
        return true;
    }

    /// <summary>
    /// Remove all signals matching a pattern from this operation.
    /// Returns the number of signals removed.
    /// </summary>
    public int RetractMatching(string pattern)
    {
        if (_signals is null) return 0;

        var removed = new List<string>();
        _signals.RemoveAll(s =>
        {
            if (StringPatternMatcher.Matches(s, pattern))
            {
                removed.Add(s);
                return true;
            }
            return false;
        });

        foreach (var signal in removed)
        {
            NotifyRetracted(signal, wasPatternMatch: true, pattern: pattern);
        }

        return removed.Count;
    }

    private void NotifyRetracted(string signal, bool wasPatternMatch, string? pattern)
    {
        if (_onSignalRetracted is null) return;

        var evt = new SignalRetractedEvent(signal, Id, Key, DateTimeOffset.UtcNow, wasPatternMatch, pattern);
        try { _onSignalRetracted(evt); }
        catch { /* Don't propagate callback exceptions */ }
    }

    /// <summary>
    /// Check if this operation has a specific signal.
    /// </summary>
    public bool HasSignal(string signal) => _signals?.Contains(signal) == true;

    // ISignalEmitter implementation
    void ISignalEmitter.Emit(string signal) => Signal(signal);
    bool ISignalEmitter.EmitCaused(string signal, SignalPropagation? cause) => EmitCaused(signal, cause);
    bool ISignalEmitter.Retract(string signal) => Retract(signal);
    int ISignalEmitter.RetractMatching(string pattern) => RetractMatching(pattern);
    bool ISignalEmitter.HasSignal(string signal) => HasSignal(signal);

    public EphemeralOperationSnapshot<TResult> ToSnapshot() =>
        new(Id, Started, Completed, Key, IsFaulted: Error != null, Error, Duration, Result, HasResult, _signals, IsPinned);

    public EphemeralOperationSnapshot ToBaseSnapshot() =>
        new(Id, Started, Completed, Key, IsFaulted: Error != null, Error, Duration, _signals, IsPinned);
}
