namespace Mostlylucid.Ephemeral.Signals;

/// <summary>
///     Naming helpers for responsibility acknowledgement signals.
/// </summary>
public static class ResponsibilitySignals
{
    /// <summary>
    ///     Default glob pattern for acknowledgement signals.
    /// </summary>
    public const string DefaultAckPattern = "responsibility.ack.*";

    /// <summary>
    ///     Builds the default acknowledgement key for an operation.
    /// </summary>
    public static string DefaultAckKey(long operationId)
    {
        return operationId.ToString();
    }
}