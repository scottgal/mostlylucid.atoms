using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.EntropyAnalysis;

/// <summary>
///     Entropy analysis for detecting bot-like patterns in sequences.
///
///     Uses Shannon entropy to measure randomness:
///     - High entropy (>3.5) = random/scanning behavior (bot)
///     - Low entropy (<0.5) = too repetitive/automated (bot)
///     - Medium entropy (0.5-3.5) = natural human patterns
///
///     Privacy-preserving: Uses XxHash64 for identity hashing.
/// </summary>
public class EntropyAnalysisAtom<T> : IAsyncDisposable where T : notnull
{
    private readonly ConcurrentDictionary<string, TrackedSequence> _sequences = new();
    private readonly TimeSpan _analysisWindow;
    private readonly int _minSamples;
    private readonly SignalSink? _signals;
    private readonly string _salt;
    private readonly Timer? _cleanupTimer;

    public EntropyAnalysisAtom(
        TimeSpan? analysisWindow = null,
        int minSamples = 5,
        SignalSink? signals = null,
        string? salt = null)
    {
        _analysisWindow = analysisWindow ?? TimeSpan.FromMinutes(15);
        _minSamples = minSamples;
        _signals = signals;
        _salt = salt ?? Guid.NewGuid().ToString();

        // Cleanup old entries every minute
        _cleanupTimer = new Timer(CleanupOldEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    ///     Record a new event in the sequence for this identity.
    /// </summary>
    public Task RecordAsync(string identityKey, T value)
    {
        var signature = HashIdentity(identityKey);
        var sequence = _sequences.GetOrAdd(signature, _ => new TrackedSequence());

        lock (sequence)
        {
            sequence.Values.Add(new TimestampedValue(value, DateTimeOffset.UtcNow));
        }

        _signals?.Raise($"entropy.recorded:{signature}");

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Calculate Shannon entropy for this identity's sequence.
    ///     Returns 0 if insufficient samples.
    /// </summary>
    public double CalculateEntropy(string identityKey)
    {
        var signature = HashIdentity(identityKey);

        if (!_sequences.TryGetValue(signature, out var sequence))
            return 0;

        List<TimestampedValue> values;
        lock (sequence)
        {
            values = sequence.Values
                .Where(v => v.Timestamp > DateTimeOffset.UtcNow - _analysisWindow)
                .ToList();
        }

        if (values.Count < _minSamples)
        {
            _signals?.Raise($"entropy.insufficient_samples:{signature}:count={values.Count}");
            return 0;
        }

        // Count frequency of each value
        var frequencies = values
            .GroupBy(v => v.Value)
            .ToDictionary(g => g.Key, g => (double)g.Count() / values.Count);

        // Calculate Shannon entropy: H = -Σ(p * log2(p))
        var entropy = 0.0;
        foreach (var freq in frequencies.Values)
        {
            if (freq > 0)
            {
                entropy -= freq * Math.Log2(freq);
            }
        }

        _signals?.Raise($"entropy.calculated:{signature}:entropy={entropy:F2}");

        return entropy;
    }

    /// <summary>
    ///     Analyze entropy and return interpretation.
    /// </summary>
    public EntropyResult Analyze(string identityKey)
    {
        var entropy = CalculateEntropy(identityKey);

        if (entropy == 0)
        {
            return new EntropyResult
            {
                Entropy = 0,
                Classification = EntropyClassification.InsufficientData,
                Confidence = 0,
                Description = "Insufficient samples for analysis"
            };
        }

        // High entropy = too random (bot scanning)
        if (entropy > 3.5)
        {
            var confidence = Math.Min(1.0, (entropy - 3.5) / 2.0); // Scale to 0-1
            _signals?.Raise($"entropy.high:{HashIdentity(identityKey)}:entropy={entropy:F2}");

            return new EntropyResult
            {
                Entropy = entropy,
                Classification = EntropyClassification.TooRandom,
                Confidence = confidence,
                Description = $"High entropy ({entropy:F2}) indicates random/scanning pattern"
            };
        }

        // Low entropy = too repetitive (bot automation)
        if (entropy < 0.5)
        {
            var confidence = Math.Min(1.0, (0.5 - entropy) / 0.5); // Scale to 0-1
            _signals?.Raise($"entropy.low:{HashIdentity(identityKey)}:entropy={entropy:F2}");

            return new EntropyResult
            {
                Entropy = entropy,
                Classification = EntropyClassification.TooRepetitive,
                Confidence = confidence,
                Description = $"Low entropy ({entropy:F2}) indicates repetitive/automated pattern"
            };
        }

        // Normal entropy = natural human patterns
        _signals?.Raise($"entropy.normal:{HashIdentity(identityKey)}:entropy={entropy:F2}");

        return new EntropyResult
        {
            Entropy = entropy,
            Classification = EntropyClassification.Normal,
            Confidence = 1.0 - Math.Abs(2.0 - entropy) / 2.0, // Highest confidence at entropy=2.0
            Description = $"Normal entropy ({entropy:F2}) indicates natural patterns"
        };
    }

    /// <summary>
    ///     Get current sample count for this identity.
    /// </summary>
    public int GetSampleCount(string identityKey)
    {
        var signature = HashIdentity(identityKey);

        if (!_sequences.TryGetValue(signature, out var sequence))
            return 0;

        lock (sequence)
        {
            return sequence.Values.Count(v => v.Timestamp > DateTimeOffset.UtcNow - _analysisWindow);
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

        foreach (var kvp in _sequences)
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
            _sequences.TryRemove(key, out _);
        }

        if (toRemove.Count > 0)
        {
            _signals?.Raise($"entropy.cleanup:removed={toRemove.Count}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleanupTimer != null)
        {
            await _cleanupTimer.DisposeAsync();
        }
    }

    private class TrackedSequence
    {
        public List<TimestampedValue> Values { get; } = new();
    }

    private record TimestampedValue(T Value, DateTimeOffset Timestamp);
}

/// <summary>
///     Result from entropy analysis.
/// </summary>
public record EntropyResult
{
    public double Entropy { get; init; }
    public EntropyClassification Classification { get; init; }
    public double Confidence { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
///     Entropy classification.
/// </summary>
public enum EntropyClassification
{
    InsufficientData,
    TooRandom,      // High entropy (> 3.5) - bot scanning
    TooRepetitive,  // Low entropy (< 0.5) - bot automation
    Normal          // Medium entropy (0.5-3.5) - human-like
}
