namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
///     Configures how operation echoes are triggered and retained.
/// </summary>
public sealed class OperationEchoMakerOptions<TPayload>
{
    /// <summary>
    ///     Optional glob-style pattern that marks an operation as echo-worthy when it emits the matching signal.
    /// </summary>
    public string? ActivationSignalPattern { get; init; }

    /// <summary>
    ///     Optional glob-style pattern that filters which signals are captured.
    /// </summary>
    public string? CaptureSignalPattern { get; init; }

    /// <summary>
    ///     Predicate invoked for each signal to decide whether it should be captured.
    /// </summary>
    public Func<SignalEvent<TPayload>, bool>? CapturePredicate { get; init; }

    /// <summary>
    ///     Whether the activation signal itself should be part of the captured payloads.
    /// </summary>
    public bool CaptureActivationSignal { get; init; } = true;

    /// <summary>
    ///     How many operations with captured signals are retained before trimming.
    /// </summary>
    public int MaxTrackedOperations { get; init; } = 256;

    /// <summary>
    ///     How long to keep capture state for operations that haven't finalized yet.
    /// </summary>
    public TimeSpan MaxCaptureAge { get; init; } = TimeSpan.FromMinutes(5);

    internal bool RequiresActivation => !string.IsNullOrWhiteSpace(ActivationSignalPattern);
}