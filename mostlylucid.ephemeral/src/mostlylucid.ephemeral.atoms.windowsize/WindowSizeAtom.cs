using System.Globalization;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.WindowSize;

/// <summary>
/// Dynamic window sizing atom that adjusts SignalSink capacity and retention based on signals.
/// Listens for command signals like "window.size.set:100" or "window.time.set:30s" to dynamically
/// tune signal window parameters at runtime without service restarts.
/// </summary>
/// <remarks>
/// <para><strong>Use Cases:</strong></para>
/// <list type="bullet">
/// <item>Adaptive memory management - increase capacity during high traffic, decrease during low</item>
/// <item>Debug mode - temporarily extend retention to capture more signal history</item>
/// <item>Circuit breaker integration - reduce window size when system is under stress</item>
/// <item>Dynamic configuration - tune parameters based on runtime metrics</item>
/// </list>
/// <para><strong>Signal Format:</strong></para>
/// <para>Capacity: <c>window.size.set:500</c>, <c>window.size.increase:50</c>, <c>window.size.decrease:50</c></para>
/// <para>Retention: <c>window.time.set:30s</c>, <c>window.time.set:500ms</c>, <c>window.time.set:00:05:00</c></para>
/// <para><strong>Thread Safety:</strong> All updates are thread-safe. Concurrent signals are handled safely,
/// though rapid updates may cause contention on SignalSink's internal lock.</para>
/// <para><strong>Performance:</strong> Signal processing is synchronous but fast (microseconds). Clamping
/// prevents invalid values from reaching the SignalSink.</para>
/// </remarks>
/// <example>
/// <code>
/// var sink = new SignalSink(maxCapacity: 100);
/// await using var atom = new WindowSizeAtom(sink);
///
/// // Dynamically increase capacity based on load
/// if (requestRate > threshold)
///     sink.Raise("window.size.set:500");
///
/// // Extend retention during debugging
/// sink.Raise("window.time.set:10m");
/// </code>
/// </example>
public sealed class WindowSizeAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly WindowSizeAtomOptions _options;

    /// <summary>
    /// Creates a new WindowSizeAtom that listens to the specified SignalSink.
    /// </summary>
    /// <param name="sink">The SignalSink to monitor and adjust. Cannot be null.</param>
    /// <param name="options">Configuration options. If null, uses defaults (capacity 16-50k, retention 5s-1h).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sink"/> is null.</exception>
    public WindowSizeAtom(SignalSink sink, WindowSizeAtomOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new WindowSizeAtomOptions();
        _sink.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent signal)
    {
        if (signal.Signal is null)
            return;

        if (HandleCapacity(signal.Signal))
            return;

        if (HandleRetention(signal.Signal))
            return;
    }

    private bool HandleCapacity(string winSignal)
    {
        if (TryParseCapacity(winSignal, _options.CapacitySetCommand, out var value))
        {
            UpdateCapacity(value);
            return true;
        }

        if (TryParseCapacity(winSignal, _options.CapacityIncreaseCommand, out value))
        {
            UpdateCapacity(_sink.MaxCapacity + value);
            return true;
        }

        if (TryParseCapacity(winSignal, _options.CapacityDecreaseCommand, out value))
        {
            UpdateCapacity(_sink.MaxCapacity - value);
            return true;
        }

        return false;
    }

    private bool HandleRetention(string winSignal)
    {
        if (TryParseTime(winSignal, _options.TimeSetCommand, out var span))
        {
            UpdateRetention(span);
            return true;
        }

        if (TryParseTime(winSignal, _options.TimeIncreaseCommand, out span))
        {
            UpdateRetention(_sink.MaxAge + span);
            return true;
        }

        if (TryParseTime(winSignal, _options.TimeDecreaseCommand, out span))
        {
            UpdateRetention(_sink.MaxAge - span);
            return true;
        }

        return false;
    }

    private void UpdateCapacity(int requested)
    {
        var clamped = Math.Min(_options.MaxCapacity, Math.Max(_options.MinCapacity, requested));
        _sink.UpdateWindowSize(maxCapacity: clamped);
    }

    private void UpdateRetention(TimeSpan requested)
    {
        var clamped = requested < _options.MinRetention
            ? _options.MinRetention
            : requested > _options.MaxRetention
                ? _options.MaxRetention
                : requested;

        _sink.UpdateWindowSize(maxAge: clamped);
    }

    private static bool TryParseCapacity(string signal, string command, out int value)
    {
        value = 0;
        if (!SignalCommandMatch.TryParse(signal, command, out var match))
            return false;

        return int.TryParse(match.Payload.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseTime(string signal, string command, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (!SignalCommandMatch.TryParse(signal, command, out var match))
            return false;

        var payload = match.Payload.Trim();
        if (TimeSpan.TryParse(payload, CultureInfo.InvariantCulture, out var span))
        {
            value = span;
            return true;
        }

        // Check "ms" before "s" to avoid matching "500ms" as "500m" + "s"
        if (payload.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(payload[..^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            value = TimeSpan.FromMilliseconds(ms);
            return true;
        }

        if (payload.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(payload[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        _sink.SignalRaised -= OnSignal;
        return default;
    }
}
