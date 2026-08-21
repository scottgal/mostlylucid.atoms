namespace Mostlylucid.Ephemeral.Atoms.Retry;

/// <summary>
///     Named backoff strategy factories. Each returns a <c>Func&lt;int, TimeSpan&gt;</c> mapping
///     attempt number (1-based) to delay — the reusable, discoverable layer over "just write a
///     lambda", without baking any interval into RetryAtom itself. Every numeric input is the
///     caller's own explicit choice; nothing here is a hidden default.
/// </summary>
public static class BackoffStrategies
{
    /// <summary>Same delay every attempt.</summary>
    public static Func<int, TimeSpan> Constant(TimeSpan delay)
    {
        return _ => delay;
    }

    /// <summary>Delay grows linearly: unit, 2*unit, 3*unit, ...</summary>
    public static Func<int, TimeSpan> Linear(TimeSpan unit)
    {
        return attempt => unit * attempt;
    }

    /// <summary>Delay doubles (or scales by <paramref name="factor" />) each attempt.</summary>
    public static Func<int, TimeSpan> Exponential(TimeSpan baseDelay, double factor = 2.0)
    {
        if (factor <= 1.0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be > 1.0.");
        return attempt => TimeSpan.FromTicks((long)(baseDelay.Ticks * Math.Pow(factor, attempt - 1)));
    }

    /// <summary>
    ///     Exponential backoff with +/- <paramref name="jitterRatio" /> random jitter, to avoid
    ///     synchronized retry storms across many callers.
    /// </summary>
    public static Func<int, TimeSpan> ExponentialWithJitter(
        TimeSpan baseDelay,
        double factor = 2.0,
        double jitterRatio = 0.2,
        Random? random = null)
    {
        if (factor <= 1.0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be > 1.0.");
        if (jitterRatio is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), jitterRatio, "Must be between 0.0 and 1.0.");

        var rng = random ?? Random.Shared;
        return attempt =>
        {
            var baseMs = baseDelay.TotalMilliseconds * Math.Pow(factor, attempt - 1);
            var jitter = baseMs * jitterRatio * (rng.NextDouble() * 2 - 1);
            return TimeSpan.FromMilliseconds(Math.Max(0, baseMs + jitter));
        };
    }
}
