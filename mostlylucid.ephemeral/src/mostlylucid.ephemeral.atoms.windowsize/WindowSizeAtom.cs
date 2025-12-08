using System.Globalization;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.WindowSize;

public sealed class WindowSizeAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly WindowSizeAtomOptions _options;
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
