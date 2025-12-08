namespace Mostlylucid.Ephemeral;

/// <summary>
///     Exposes pin/unpin semantics that operations can use for responsibility tracking.
/// </summary>
public interface IOperationPinning
{
    /// <summary>
    ///     Pin an operation so it survives window eviction.
    /// </summary>
    bool Pin(long operationId);

    /// <summary>
    ///     Unpin an operation so it can be cleaned up normally.
    /// </summary>
    bool Unpin(long operationId);
}