namespace Mostlylucid.Ephemeral;

/// <summary>
///     Core interface for ephemeral operations, defining properties needed by CoordinatorBase.
/// </summary>
public interface IEphemeralOperationCore : ISignalSource
{
    /// <summary>
    ///     Unique operation ID.
    /// </summary>
    long Id { get; }

    /// <summary>
    ///     Operation start time.
    /// </summary>
    DateTimeOffset Started { get; }

    /// <summary>
    ///     Operation completion time (null if still running).
    /// </summary>
    DateTimeOffset? Completed { get; set; }

    /// <summary>
    ///     Optional key for keyed coordinators.
    /// </summary>
    string? Key { get; init; }

    /// <summary>
    ///     When true, this operation survives time and size evictions.
    /// </summary>
    bool IsPinned { get; set; }

    /// <summary>
    ///     Converts to a snapshot for external consumption.
    /// </summary>
    EphemeralOperationSnapshot ToSnapshot();
}
