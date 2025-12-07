using System.Collections.Concurrent;
using System.Threading;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Typed signal with payload for evidence-rich emissions.
/// </summary>
public readonly record struct SignalEvent<TPayload>(
    string Signal,
    long OperationId,
    string? Key,
    DateTimeOffset Timestamp,
    TPayload Payload,
    SignalPropagation? Propagation = null)
{
    public int Depth => Propagation?.Depth ?? 0;
    public SignalEvent WithoutPayload() => new(Signal, OperationId, Key, Timestamp, Propagation);
}

/// <summary>
/// Lightweight typed sink that forwards to an inner untyped sink for compatibility.
/// </summary>
public sealed class TypedSignalSink<TPayload>
{
    private readonly ConcurrentQueue<SignalEvent<TPayload>> _window = new();
    private readonly int _maxCapacity;
    private readonly TimeSpan _maxAge;
    private readonly SignalSink _inner;
    private long _raiseCounter;

    public TypedSignalSink(SignalSink? inner = null, int maxCapacity = 512, TimeSpan? maxAge = null)
    {
        _inner = inner ?? new SignalSink();
        _maxCapacity = maxCapacity;
        _maxAge = maxAge ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Underlying untyped sink (for existing APIs).
    /// </summary>
    public SignalSink Untyped => _inner;

    /// <summary>
    /// Raise a typed signal (also forwards an untyped copy).
    /// </summary>
    public void Raise(SignalEvent<TPayload> evt)
    {
        _window.Enqueue(evt);
        _inner.Raise(evt.WithoutPayload());

        if ((Interlocked.Increment(ref _raiseCounter) & 0x3FF) == 0)
            Cleanup();
    }

    /// <summary>
    /// Raise a typed signal with generated IDs/timestamp.
    /// </summary>
    public void Raise(string signal, TPayload payload, string? key = null, SignalPropagation? propagation = null)
    {
        Raise(new SignalEvent<TPayload>(signal, EphemeralIdGenerator.NextId(), key, DateTimeOffset.UtcNow, payload, propagation));
    }

    /// <summary>
    /// Sense typed signals (snapshot).
    /// </summary>
    public IReadOnlyList<SignalEvent<TPayload>> Sense(Func<SignalEvent<TPayload>, bool>? predicate = null)
    {
        if (predicate is null)
            return _window.ToArray();

        var results = new List<SignalEvent<TPayload>>(Math.Min(_window.Count, 64));
        foreach (var s in _window)
        {
            if (predicate(s))
                results.Add(s);
        }
        return results;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - _maxAge;

        while (_window.Count > _maxCapacity && _window.TryDequeue(out _)) { }
        while (_window.TryPeek(out var head) && head.Timestamp < cutoff)
        {
            _window.TryDequeue(out _);
        }
    }
}
