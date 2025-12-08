namespace Mostlylucid.Ephemeral.Atoms.RateLimit;

/// <summary>
/// Configuration for <see cref="RateLimitAtom"/>.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Tokens per second. Default: 10.
    /// </summary>
    public double InitialRatePerSecond { get; set; } = 10;

    /// <summary>
    /// Burst capacity. Default: 20.
    /// </summary>
    public int Burst { get; set; } = 20;

    /// <summary>
    /// Signal pattern used to adjust the rate. Default: `rate.limit.*`.
    /// </summary>
    public string ControlSignalPattern { get; set; } = "rate.limit.*";
}
