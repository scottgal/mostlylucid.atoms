using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Simulated atom for demonstration purposes.
/// Listens for configured signals, performs simulated work (Task.Delay), and emits response signals.
/// Demonstrates the "signal is context, atom holds state" pattern.
/// </summary>
public class TestAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly string _name;
    private readonly List<string> _listenSignals;
    private readonly Dictionary<string, string> _signalResponses;
    private readonly TimeSpan _processingDelay;

    // State storage - demonstrates atom holds authoritative state
    private int _processedCount = 0;
    private string _lastProcessedSignal = "";
    private DateTime _lastProcessedTime = DateTime.MinValue;
    private readonly List<string> _processingHistory = new();
    private bool _isBusy = false;

    public TestAtom(
        SignalSink sink,
        string name,
        List<string> listenSignals,
        Dictionary<string, string>? signalResponses = null,
        TimeSpan? processingDelay = null)
    {
        _sink = sink;
        _name = name;
        _listenSignals = listenSignals;
        _signalResponses = signalResponses ?? new();
        _processingDelay = processingDelay ?? TimeSpan.FromMilliseconds(100);

        _sink.SignalRaised += OnSignal;
    }

    private async void OnSignal(SignalEvent signal)
    {
        // Check if we listen for this signal
        var shouldProcess = _listenSignals.Any(pattern =>
            StringPatternMatcher.Matches(signal.Signal, pattern));

        if (!shouldProcess)
            return;

        _isBusy = true;

        try
        {
            // Simulate work
            await Task.Delay(_processingDelay);

            // Update state in atom
            _processedCount++;
            _lastProcessedSignal = signal.Signal;
            _lastProcessedTime = DateTime.UtcNow;
            _processingHistory.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {_name} processed: {signal.Signal}");

            // Emit response signals if configured
            foreach (var kvp in _signalResponses)
            {
                if (StringPatternMatcher.Matches(signal.Signal, kvp.Key))
                {
                    // Small delay to simulate processing chain
                    await Task.Delay(50);
                    _sink.Raise(kvp.Value);
                }
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    // State queries - listeners query these for current truth
    public int GetProcessedCount() => _processedCount;
    public string GetLastProcessedSignal() => _lastProcessedSignal;
    public DateTime GetLastProcessedTime() => _lastProcessedTime;
    public bool IsBusy() => _isBusy;
    public IReadOnlyList<string> GetProcessingHistory() => _processingHistory;

    public string GetName() => _name;
    public IReadOnlyList<string> GetListenSignals() => _listenSignals;

    // Composite query
    public TestAtomState GetState() => new()
    {
        Name = _name,
        ProcessedCount = _processedCount,
        LastProcessedSignal = _lastProcessedSignal,
        LastProcessedTime = _lastProcessedTime,
        IsBusy = _isBusy,
        ListenSignals = _listenSignals,
        HistoryCount = _processingHistory.Count
    };

    public ValueTask DisposeAsync()
    {
        _sink.SignalRaised -= OnSignal;
        return default;
    }
}

public record TestAtomState
{
    public string Name { get; init; } = "";
    public int ProcessedCount { get; init; }
    public string LastProcessedSignal { get; init; } = "";
    public DateTime LastProcessedTime { get; init; }
    public bool IsBusy { get; init; }
    public IReadOnlyList<string> ListenSignals { get; init; } = Array.Empty<string>();
    public int HistoryCount { get; init; }
}
