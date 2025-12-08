namespace Mostlylucid.Ephemeral;

/// <summary>
///     Exposes the finalization event that fires when an operation leaves the coordinator window.
/// </summary>
public interface IOperationFinalization
{
    /// <summary>
    ///     Raised when an operation is evicted from the window (trimmed, aged out, or explicitly evicted).
    /// </summary>
    event Action<EphemeralOperationSnapshot>? OperationFinalized;
}