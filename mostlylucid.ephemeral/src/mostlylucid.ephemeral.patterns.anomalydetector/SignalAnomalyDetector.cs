namespace Mostlylucid.Ephemeral.Patterns.AnomalyDetector;

/// <summary>
///     Scans a SignalSink with a moving time window and flags anomalies based on pattern + threshold.
/// </summary>
public sealed class SignalAnomalyDetector
{
    private readonly string _pattern;
    private readonly SignalSink _sink;
    private readonly int _threshold;
    private readonly TimeSpan _window;

    public SignalAnomalyDetector(
        SignalSink sink,
        string pattern = "error.*",
        int threshold = 5,
        TimeSpan? window = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));

        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _threshold = threshold;
        _window = window ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    ///     Checks the current window for anomaly (count of matches >= threshold).
    /// </summary>
    public bool IsAnomalous()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        var recent = _sink.Sense(s => s.Timestamp >= cutoff);
        var matches = 0;
        foreach (var evt in recent)
            if (StringPatternMatcher.Matches(evt.Signal, _pattern))
            {
                matches++;
                if (matches >= _threshold) return true;
            }

        return false;
    }

    /// <summary>
    ///     Get the current count of matching signals in the window.
    /// </summary>
    public int GetMatchCount()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        var recent = _sink.Sense(s => s.Timestamp >= cutoff);
        return recent.Count(evt => StringPatternMatcher.Matches(evt.Signal, _pattern));
    }
}