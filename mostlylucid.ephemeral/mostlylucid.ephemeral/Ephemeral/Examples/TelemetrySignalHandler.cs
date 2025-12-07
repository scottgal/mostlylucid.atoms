namespace Mostlylucid.Helpers.Ephemeral.Examples;

/// <summary>
/// Example showing async signal handling with telemetry integration.
/// Uses AsyncSignalProcessor for non-blocking I/O-bound signal processing.
/// </summary>
public class TelemetrySignalHandler : IAsyncDisposable
{
    private readonly AsyncSignalProcessor _processor;
    private readonly ITelemetryClient _telemetry;

    public TelemetrySignalHandler(ITelemetryClient telemetry)
    {
        _telemetry = telemetry;
        _processor = new AsyncSignalProcessor(
            HandleSignalAsync,
            maxConcurrency: 8,
            maxQueueSize: 5000);
    }

    /// <summary>
    /// Synchronous entry point for signal dispatch (called from OnSignal callback).
    /// Returns immediately; processing happens in background.
    /// </summary>
    public bool OnSignal(SignalEvent signal) => _processor.Enqueue(signal);

    /// <summary>
    /// Async handler for each signal.
    /// </summary>
    private async Task HandleSignalAsync(SignalEvent signal, CancellationToken ct)
    {
        var properties = new Dictionary<string, string>
        {
            ["signal"] = signal.Signal,
            ["operationId"] = signal.OperationId.ToString(),
            ["key"] = signal.Key ?? "none",
            ["depth"] = signal.Depth.ToString()
        };

        await _telemetry.TrackEventAsync("EphemeralSignal", properties, ct);

        // Categorized tracking
        if (signal.StartsWith("error"))
            await _telemetry.TrackExceptionAsync(signal.Signal, properties, ct);
        else if (signal.StartsWith("perf"))
            await _telemetry.TrackMetricAsync(signal.Signal, 1, ct);
    }

    // Expose stats for monitoring
    public int QueuedCount => _processor.QueuedCount;
    public long ProcessedCount => _processor.ProcessedCount;
    public long DroppedCount => _processor.DroppedCount;

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
    }
}

/// <summary>
/// Interface for telemetry client (for testing/mocking).
/// </summary>
public interface ITelemetryClient
{
    Task TrackEventAsync(string eventName, Dictionary<string, string> properties, CancellationToken ct);
    Task TrackExceptionAsync(string exceptionType, Dictionary<string, string> properties, CancellationToken ct);
    Task TrackMetricAsync(string metricName, double value, CancellationToken ct);
}

/// <summary>
/// Simple in-memory telemetry client for testing.
/// </summary>
public class InMemoryTelemetryClient : ITelemetryClient
{
    private readonly List<TelemetryEvent> _events = new();
    private readonly object _lock = new();

    public Task TrackEventAsync(string eventName, Dictionary<string, string> properties, CancellationToken ct)
    {
        lock (_lock)
        {
            _events.Add(new TelemetryEvent(eventName, TelemetryEventType.Event, properties));
        }
        return Task.CompletedTask;
    }

    public Task TrackExceptionAsync(string exceptionType, Dictionary<string, string> properties, CancellationToken ct)
    {
        lock (_lock)
        {
            _events.Add(new TelemetryEvent(exceptionType, TelemetryEventType.Exception, properties));
        }
        return Task.CompletedTask;
    }

    public Task TrackMetricAsync(string metricName, double value, CancellationToken ct)
    {
        lock (_lock)
        {
            _events.Add(new TelemetryEvent(metricName, TelemetryEventType.Metric, new Dictionary<string, string> { ["value"] = value.ToString() }));
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<TelemetryEvent> GetEvents()
    {
        lock (_lock)
        {
            return _events.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}

public record TelemetryEvent(string Name, TelemetryEventType Type, Dictionary<string, string> Properties);

public enum TelemetryEventType
{
    Event,
    Exception,
    Metric
}
