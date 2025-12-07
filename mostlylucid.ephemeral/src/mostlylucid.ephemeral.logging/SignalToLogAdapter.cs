using Microsoft.Extensions.Logging;

namespace Mostlylucid.Ephemeral.Logging;

/// <summary>
/// Bridges signals back into ILogger. Useful when you want signal-driven workflows to still show up in standard logs.
/// </summary>
public sealed class SignalToLoggerAdapter : IDisposable
{
    private readonly SignalSink _sink;
    private readonly ILogger _logger;
    private readonly SignalToLogOptions _options;
    private bool _disposed;

    public SignalToLoggerAdapter(SignalSink sink, ILogger logger, SignalToLogOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SignalToLogOptions();

        _sink.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent evt)
    {
        var mapped = _options.Map(evt);
        if (mapped is null)
            return;

        _logger.Log(mapped.Value.Level, mapped.Value.EventId, mapped.Value.Message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sink.SignalRaised -= OnSignal;
    }
}

public sealed class SignalToLogOptions
{
    /// <summary>
    /// Map a signal to a log message. Return null to ignore.
    /// </summary>
    public Func<SignalEvent, LogMessage?> Map { get; set; } = DefaultMap;

    private static LogMessage? DefaultMap(SignalEvent evt)
    {
        // Heuristic: level inferred from first segment, otherwise Information.
        var level = LogLevel.Information;
        var name = evt.Signal?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(name))
        {
            var first = name.Split('.', ':')[0].ToLowerInvariant();
            level = first switch
            {
                "fatal" or "critical" => LogLevel.Critical,
                "error" => LogLevel.Error,
                "warn" or "warning" => LogLevel.Warning,
                "debug" => LogLevel.Debug,
                "trace" => LogLevel.Trace,
                _ => LogLevel.Information
            };
        }

        var eventId = new EventId((int)(evt.OperationId % int.MaxValue), evt.Key ?? evt.Signal);
        var message = $"{evt.Signal} (op:{evt.OperationId}, key:{evt.Key ?? "none"})";
        return new LogMessage(level, eventId, message);
    }
}

public readonly record struct LogMessage(LogLevel Level, EventId EventId, string Message);
