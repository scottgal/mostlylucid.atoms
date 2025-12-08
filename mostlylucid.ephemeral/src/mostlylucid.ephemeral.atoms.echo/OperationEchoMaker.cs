using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
///     Watches typed signals and the coordinator finalization event to build compact operation echoes.
/// </summary>
public sealed class OperationEchoMaker<TPayload> : IDisposable
{
    private readonly Func<string, bool>? _activationMatcher;
    private readonly Func<string, bool>? _captureMatcher;
    private readonly Func<SignalEvent<TPayload>, bool> _capturePredicate;
    private readonly ConcurrentDictionary<long, CaptureEntry> _captures = new();
    private readonly IOperationFinalization _finalizer;
    private readonly Action<OperationEchoEntry<TPayload>>? _onEcho;
    private readonly OperationEchoMakerOptions<TPayload> _options;
    private readonly ConcurrentQueue<long> _order = new();
    private readonly object _trimLock = new();
    private readonly TypedSignalSink<TPayload> _typedSink;
    private bool _disposed;

    public OperationEchoMaker(
        TypedSignalSink<TPayload> typedSink,
        IOperationFinalization finalizer,
        Action<OperationEchoEntry<TPayload>>? onEcho = null,
        OperationEchoMakerOptions<TPayload>? options = null)
    {
        _typedSink = typedSink ?? throw new ArgumentNullException(nameof(typedSink));
        _finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));
        _options = options ?? new OperationEchoMakerOptions<TPayload>();
        _activationMatcher = BuildMatcher(_options.ActivationSignalPattern);
        _captureMatcher = BuildMatcher(_options.CaptureSignalPattern);
        _capturePredicate = _options.CapturePredicate ?? (_ => true);
        _onEcho = onEcho;

        _typedSink.TypedSignalRaised += OnSignal;
        _finalizer.OperationFinalized += OnFinalized;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _typedSink.TypedSignalRaised -= OnSignal;
        _finalizer.OperationFinalized -= OnFinalized;
        _disposed = true;
    }

    /// <summary>
    ///     Raised when an operation echo is emitted.
    /// </summary>
    public event Action<OperationEchoEntry<TPayload>>? EchoCreated;

    private static Func<string, bool>? BuildMatcher(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        return signal => StringPatternMatcher.Matches(signal, pattern);
    }

    private void OnSignal(SignalEvent<TPayload> signal)
    {
        if (_disposed)
            return;

        var entry = _captures.GetOrAdd(signal.OperationId, _ => new CaptureEntry(signal.Timestamp));
        entry.LastUpdated = signal.Timestamp;

        var isActivation = _activationMatcher?.Invoke(signal.Signal) == true;
        if (isActivation) entry.Activated = true;

        if (_options.RequiresActivation && !entry.Activated)
            return;

        if (isActivation && !_options.CaptureActivationSignal && _options.RequiresActivation)
            return;

        if (!ShouldCapture(signal))
            return;

        entry.Captures.Add(new OperationEchoCapture<TPayload>(signal.Signal, signal.Payload, signal.Timestamp));
        EnqueueOrder(signal.OperationId);
        TrimCapturedState();
    }

    private bool ShouldCapture(SignalEvent<TPayload> signal)
    {
        if (_captureMatcher is { } matcher && !matcher(signal.Signal))
            return false;

        return _capturePredicate(signal);
    }

    private void OnFinalized(EphemeralOperationSnapshot snapshot)
    {
        if (_disposed)
            return;

        if (!_captures.TryRemove(snapshot.OperationId, out var entry))
            return;

        if (entry.Captures.Count == 0)
            return;

        var echo = new OperationEchoEntry<TPayload>(
            snapshot.OperationId,
            snapshot.Key,
            snapshot.IsPinned,
            entry.Captures.ToArray(),
            snapshot.Completed ?? DateTimeOffset.UtcNow);

        _onEcho?.Invoke(echo);
        EchoCreated?.Invoke(echo);
        TrimCapturedState();
    }

    private void EnqueueOrder(long operationId)
    {
        _order.Enqueue(operationId);
    }

    private void TrimCapturedState()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_trimLock)
        {
            while (_captures.Count > Math.Max(1, _options.MaxTrackedOperations))
            {
                if (!_order.TryDequeue(out var oldest))
                    break;
                _captures.TryRemove(oldest, out _);
            }

            if (_options.MaxCaptureAge <= TimeSpan.Zero)
                return;

            while (_order.TryPeek(out var candidate))
            {
                if (!_captures.TryGetValue(candidate, out var entry))
                {
                    _order.TryDequeue(out _);
                    continue;
                }

                if (!entry.IsExpired(now, _options.MaxCaptureAge))
                    break;

                _order.TryDequeue(out _);
                _captures.TryRemove(candidate, out _);
            }
        }
    }

    private sealed class CaptureEntry
    {
        public CaptureEntry(DateTimeOffset timestamp)
        {
            LastUpdated = timestamp;
        }

        public List<OperationEchoCapture<TPayload>> Captures { get; } = new();
        public bool Activated { get; set; }
        public DateTimeOffset LastUpdated { get; set; }

        public bool IsExpired(DateTimeOffset now, TimeSpan maxAge)
        {
            return maxAge > TimeSpan.Zero && now - LastUpdated > maxAge;
        }
    }
}