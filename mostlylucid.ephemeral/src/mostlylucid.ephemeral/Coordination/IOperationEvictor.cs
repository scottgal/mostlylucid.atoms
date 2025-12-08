namespace Mostlylucid.Ephemeral;

/// <summary>
///     Allows explicit eviction of operations from the coordinator window.
/// </summary>
public interface IOperationEvictor
{
    /// <summary>
    ///     Tries to remove the operation with the provided ID immediately.
    ///     Returns true when the operation was found and evicted.
    /// </summary>
    bool TryKill(long operationId);
}
