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
    ///     Raise a signal on this operation.
    /// </summary>
    void Emit(string signal);

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
///     Global signal sink. Operations raise signals here.
///     Process with another coordinator or poll the window.
/// </summary>
public sealed class SignalSink
{
        private long _maxAgeTicks;
        private int _maxCapacity;
    private readonly ConcurrentQueue<SignalEvent> _window = new();
    private long _raiseCounter;

    private readonly object _windowSizeLock = new();

    public SignalSink(int maxCapacity = 1000, TimeSpan? maxAge = null)
    {
        _maxCapacity = maxCapacity;
        _maxAgeTicks = (maxAge ?? TimeSpan.FromMinutes(1)).Ticks;
    }

    public int MaxCapacity => Volatile.Read(ref _maxCapacity);

    public TimeSpan MaxAge => TimeSpan.FromTicks(Volatile.Read(ref _maxAgeTicks));

    public void UpdateWindowSize(int? maxCapacity = null, TimeSpan? maxAge = null)
    {
        lock (_windowSizeLock)
        {
            if (maxCapacity.HasValue)
                Interlocked.Exchange(ref _maxCapacity, Math.Max(1, maxCapacity.Value));

            if (maxAge.HasValue && maxAge.Value > TimeSpan.Zero)
                Interlocked.Exchange(ref _maxAgeTicks, maxAge.Value.Ticks);
        }
    }

    /// <summary>
    ///     Approximate count of signals in the window.
    ///     May include some expired signals between cleanup cycles.
    /// </summary>
    public int Count => _window.Count;

    /// <summary>
    ///     Raised immediately whenever a signal is enqueued. Use for live subscribers that need push semantics.
    /// </summary>
    public event Action<SignalEvent>? SignalRaised;

    /// <summary>
    ///     Raise a signal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Raise(SignalEvent signal)
    {
        _window.Enqueue(signal);
        try
        {
            SignalRaised?.Invoke(signal);
        }
        catch
        {
            /* never throw from signal fan-out */
        }

        // Only cleanup every ~1024 calls to avoid contention
        if ((Interlocked.Increment(ref _raiseCounter) & 0x3FF) == 0)
            Cleanup();
    }

    /// <summary>
    ///     Raise a signal with just the name (generates ID and timestamp).
    /// </summary>
    public void Raise(string signal, string? key = null)
    {
        Raise(new SignalEvent(signal, EphemeralIdGenerator.NextId(), key, DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Sense all visible signals.
    ///     Since cleanup runs periodically, most signals in the queue are valid.
    ///     Returns a snapshot - the queue may continue to change.
    /// </summary>
    public IReadOnlyList<SignalEvent> Sense()
    {
        // Rely on periodic cleanup; avoid O(n) age check on hot path
        return _window.ToArray();
    }

    /// <summary>
    ///     Sense signals matching predicate.
    /// </summary>
    public IReadOnlyList<SignalEvent> Sense(Func<SignalEvent, bool> predicate)
    {
        // Use pre-sized list to reduce allocations for common cases
        var results = new List<SignalEvent>(Math.Min(_window.Count, 64));
        foreach (var s in _window)
            if (predicate(s))
                results.Add(s);
        return results;
    }

    /// <summary>
    ///     Detect any signals matching predicate.
    ///     Short-circuits on first match for O(1) best case.
    /// </summary>
    public bool Detect(Func<SignalEvent, bool> predicate)
    {
        foreach (var s in _window)
            if (predicate(s))
                return true;
        return false;
    }

    /// <summary>
    ///     Detect any signals with name.
    ///     Optimized for exact string match - short-circuits on first match.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Detect(string signalName)
    {
        foreach (var s in _window)
            if (s.Signal == signalName)
                return true;
        return false;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        var maxCapacity = MaxCapacity;

        // Size-based - limit iterations to prevent unbounded cleanup
        var removed = 0;
        while (_window.Count > maxCapacity && removed < 1000 && _window.TryDequeue(out _))
        {
            removed++;
        }

        // Age-based - use safe TryDequeue pattern
        removed = 0;
        while (removed < 1000 && _window.TryDequeue(out var item))
        {
            if (item.Timestamp >= cutoff)
            {
                // Put it back if not expired - relies on timestamp ordering
                // Note: This is a best-effort approach; concurrent modifications may skip items
                break;
            }
            removed++;
        }
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
