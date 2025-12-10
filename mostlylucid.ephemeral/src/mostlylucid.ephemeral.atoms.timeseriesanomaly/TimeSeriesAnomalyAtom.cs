using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.TimeSeriesAnomaly;

/// <summary>
///     Time-series anomaly detection using Z-score statistical analysis.
///
///     Detects outliers that are > N standard deviations from the mean.
///     Default threshold: 3.0 (99.7% confidence interval).
///
///     Privacy-preserving: Uses XxHash64 for identity hashing.
/// </summary>
public class TimeSeriesAnomalyAtom : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TrackedTimeSeries> _series = new();
    private readonly TimeSpan _analysisWindow;
    private readonly int _minSamples;
    private readonly double _zScoreThreshold;
    private readonly SignalSink? _signals;
    private readonly string _salt;
    private readonly Timer? _cleanupTimer;

    public TimeSeriesAnomalyAtom(
        TimeSpan? analysisWindow = null,
        int minSamples = 10,
        double zScoreThreshold = 3.0,
        SignalSink? signals = null,
        string? salt = null)
    {
        _analysisWindow = analysisWindow ?? TimeSpan.FromMinutes(15);
        _minSamples = minSamples;
        _zScoreThreshold = zScoreThreshold;
        _signals = signals;
        _salt = salt ?? Guid.NewGuid().ToString();

        _cleanupTimer = new Timer(CleanupOldEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    ///     Record a value and check for anomaly in one call.
    /// </summary>
    public AnomalyResult DetectAnomaly(string identityKey, double value)
    {
        var signature = HashIdentity(identityKey);
        var series = _series.GetOrAdd(signature, _ => new TrackedTimeSeries());

        lock (series)
        {
            var recentValues = series.Values
                .Where(v => v.Timestamp > DateTimeOffset.UtcNow - _analysisWindow)
                .Select(v => v.Value)
                .ToList();

            // Not enough historical data
            if (recentValues.Count < _minSamples)
            {
                series.Values.Add(new TimestampedValue(value, DateTimeOffset.UtcNow));
                _signals?.Raise($"anomaly.insufficient_data:{signature}:samples={recentValues.Count}");

                return new AnomalyResult
                {
                    IsAnomalous = false,
                    ZScore = 0,
                    Mean = 0,
                    StandardDeviation = 0,
                    Description = $"Insufficient data ({recentValues.Count}/{_minSamples} samples)"
                };
            }

            // Calculate statistics
            var mean = recentValues.Average();
            var variance = recentValues.Select(v => Math.Pow(v - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            // Avoid division by zero
            if (stdDev < 0.001)
            {
                series.Values.Add(new TimestampedValue(value, DateTimeOffset.UtcNow));
                _signals?.Raise($"anomaly.constant_values:{signature}");

                return new AnomalyResult
                {
                    IsAnomalous = false,
                    ZScore = 0,
                    Mean = mean,
                    StandardDeviation = stdDev,
                    Description = "Constant values (stddev near zero)"
                };
            }

            // Calculate Z-score for current value
            var zScore = Math.Abs((value - mean) / stdDev);

            // Record the value
            series.Values.Add(new TimestampedValue(value, DateTimeOffset.UtcNow));

            // Check if anomalous
            var isAnomalous = zScore > _zScoreThreshold;

            if (isAnomalous)
            {
                _signals?.Raise($"anomaly.detected:{signature}:zscore={zScore:F2}:value={value:F2}:mean={mean:F2}");

                return new AnomalyResult
                {
                    IsAnomalous = true,
                    ZScore = zScore,
                    Mean = mean,
                    StandardDeviation = stdDev,
                    Description = $"Anomaly detected: {value:F2} vs {mean:F2}±{stdDev:F2} (z={zScore:F1})"
                };
            }

            _signals?.Raise($"anomaly.normal:{signature}:zscore={zScore:F2}");

            return new AnomalyResult
            {
                IsAnomalous = false,
                ZScore = zScore,
                Mean = mean,
                StandardDeviation = stdDev,
                Description = "Normal value within expected range"
            };
        }
    }

    /// <summary>
    ///     Just record a value without checking for anomaly.
    /// </summary>
    public void RecordValue(string identityKey, double value)
    {
        var signature = HashIdentity(identityKey);
        var series = _series.GetOrAdd(signature, _ => new TrackedTimeSeries());

        lock (series)
        {
            series.Values.Add(new TimestampedValue(value, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    ///     Get current statistics for this identity.
    /// </summary>
    public (double Mean, double StdDev, int SampleCount) GetStatistics(string identityKey)
    {
        var signature = HashIdentity(identityKey);

        if (!_series.TryGetValue(signature, out var series))
            return (0, 0, 0);

        lock (series)
        {
            var recentValues = series.Values
                .Where(v => v.Timestamp > DateTimeOffset.UtcNow - _analysisWindow)
                .Select(v => v.Value)
                .ToList();

            if (recentValues.Count < 2)
                return (0, 0, recentValues.Count);

            var mean = recentValues.Average();
            var variance = recentValues.Select(v => Math.Pow(v - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            return (mean, stdDev, recentValues.Count);
        }
    }

    private string HashIdentity(string identityKey)
    {
        var salted = $"{identityKey}:{_salt}";
        var bytes = Encoding.UTF8.GetBytes(salted);
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    private void CleanupOldEntries(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow - _analysisWindow - TimeSpan.FromMinutes(5);
        var toRemove = new List<string>();

        foreach (var kvp in _series)
        {
            lock (kvp.Value)
            {
                kvp.Value.Values.RemoveAll(v => v.Timestamp < cutoff);

                if (kvp.Value.Values.Count == 0)
                {
                    toRemove.Add(kvp.Key);
                }
            }
        }

        foreach (var key in toRemove)
        {
            _series.TryRemove(key, out _);
        }

        if (toRemove.Count > 0)
        {
            _signals?.Raise($"anomaly.cleanup:removed={toRemove.Count}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleanupTimer != null)
        {
            await _cleanupTimer.DisposeAsync();
        }
    }

    private class TrackedTimeSeries
    {
        public List<TimestampedValue> Values { get; } = new();
    }

    private record TimestampedValue(double Value, DateTimeOffset Timestamp);
}

/// <summary>
///     Result from anomaly detection.
/// </summary>
public record AnomalyResult
{
    public bool IsAnomalous { get; init; }
    public double ZScore { get; init; }
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public string Description { get; init; } = string.Empty;
}
