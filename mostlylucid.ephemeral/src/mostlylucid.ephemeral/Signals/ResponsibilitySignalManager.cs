using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral.Signals;

/// <summary>
///     Helps atoms pin their operations until a downstream consumer acknowledges them via signals.
/// </summary>
public sealed class ResponsibilitySignalManager : IDisposable
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan? _maxPinDuration;
    private readonly IOperationPinning _pinning;
    private readonly ConcurrentDictionary<long, ResponsibilityRegistration> _registrations = new();
    private readonly SignalSink _signals;

    /// <summary>
    ///     Creates a manager for a coordinator and shared signal sink.
    /// </summary>
    public ResponsibilitySignalManager(IOperationPinning pinning, SignalSink signals, TimeSpan? maxPinDuration = null,
        Func<DateTimeOffset>? clock = null)
    {
        _pinning = pinning ?? throw new ArgumentNullException(nameof(pinning));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _maxPinDuration = maxPinDuration;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _signals.SignalRaised += OnSignalRaised;
    }

    /// <summary>
    ///     Number of pending responsibility registrations.
    /// </summary>
    public int PendingCount => _registrations.Count;

    public void Dispose()
    {
        _signals.SignalRaised -= OnSignalRaised;
        foreach (var registration in _registrations.Values) _pinning.Unpin(registration.OperationId);
        _registrations.Clear();
    }

    /// <summary>
    ///     Pin an operation and release it when the specified ack signal is raised with the optional key.
    /// </summary>
    public bool PinUntilQueried(long operationId, string ackSignalPattern)
    {
        return PinUntilQueried(operationId, ackSignalPattern, operationId.ToString());
    }

    /// <summary>
    ///     Pin an operation and release it when the specified ack signal is raised with the provided key.
    /// </summary>
    public bool PinUntilQueried(long operationId, string ackSignalPattern, string ackKey, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(ackSignalPattern))
            throw new ArgumentException("Ack pattern is required.", nameof(ackSignalPattern));

        CleanupExpired(_clock());

        if (!_pinning.Pin(operationId))
            return false;

        var registration = new ResponsibilityRegistration(operationId, ackSignalPattern, ackKey, _clock(), description);
        _registrations[operationId] = registration;
        return true;
    }

    private void OnSignalRaised(SignalEvent evt)
    {
        var now = _clock();
        CleanupExpired(now);

        foreach (var registration in _registrations.Values)
        {
            if (!StringPatternMatcher.Matches(evt.Signal, registration.AckPattern))
                continue;

            if (registration.AckKey is not null && registration.AckKey != evt.Key)
                continue;

            if (_registrations.TryRemove(registration.OperationId, out _)) _pinning.Unpin(registration.OperationId);
        }
    }

    /// <summary>
    ///     Force complete a responsibility so the operation can be evicted.
    /// </summary>
    public bool CompleteResponsibility(long operationId)
    {
        return _registrations.TryRemove(operationId, out _) && _pinning.Unpin(operationId);
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        if (_maxPinDuration is not { } maxDuration)
            return;

        foreach (var registration in _registrations.Values)
        {
            if (now - registration.PinnedAt <= maxDuration)
                continue;

            if (_registrations.TryRemove(registration.OperationId, out _))
                _pinning.Unpin(registration.OperationId);
        }
    }

    /// <summary>
    ///     Gets all currently pinned responsibilities.
    /// </summary>
    public IReadOnlyCollection<ResponsibilitySnapshot> GetActiveResponsibilities()
    {
        return _registrations.Values
            .Select(r => new ResponsibilitySnapshot(r.OperationId, r.AckPattern, r.AckKey, r.Description, r.PinnedAt))
            .ToArray();
    }

    private sealed record ResponsibilityRegistration(
        long OperationId,
        string AckPattern,
        string? AckKey,
        DateTimeOffset PinnedAt,
        string? Description);

    /// <summary>
    ///     Snapshot of an outstanding responsibility registration.
    /// </summary>
    public sealed record ResponsibilitySnapshot(
        long OperationId,
        string AckPattern,
        string? AckKey,
        string? Description,
        DateTimeOffset PinnedAt);
}