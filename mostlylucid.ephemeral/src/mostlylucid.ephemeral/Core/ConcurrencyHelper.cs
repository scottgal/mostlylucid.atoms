namespace Mostlylucid.Ephemeral;

/// <summary>
///     Shared utilities for concurrency management across coordinators and atoms.
/// </summary>
public static class ConcurrencyHelper
{
    /// <summary>
    ///     Resolves concurrency to default (ProcessorCount) if not specified.
    /// </summary>
    public static int ResolveDefaultConcurrency(int? maxConcurrency)
    {
        return maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount;
    }

    /// <summary>
    ///     Creates a concurrency gate based on options.
    ///     Returns adjustable gate if dynamic concurrency enabled, otherwise fixed.
    /// </summary>
    public static IConcurrencyGate CreateGate(EphemeralOptions options)
    {
        return options.EnableDynamicConcurrency
            ? new AdjustableConcurrencyGate(options.MaxConcurrency)
            : new FixedConcurrencyGate(options.MaxConcurrency);
    }
}
