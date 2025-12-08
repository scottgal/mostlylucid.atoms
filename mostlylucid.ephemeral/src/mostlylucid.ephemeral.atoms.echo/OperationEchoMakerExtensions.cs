namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
///     Convenience helpers to hook the echo maker into any coordinator that exposes the finalization event.
/// </summary>
public static class OperationEchoMakerExtensions
{
    /// <summary>
    ///     Starts an echo maker that persists echoes through an <see cref="OperationEchoAtom{TPayload}" />.
    /// </summary>
    public static OperationEchoMaker<TPayload> EnableOperationEchoing<TPayload>(
        this IOperationFinalization coordinator,
        TypedSignalSink<TPayload> typedSink,
        OperationEchoAtom<TPayload> atom,
        OperationEchoMakerOptions<TPayload>? options = null)
    {
        if (atom is null) throw new ArgumentNullException(nameof(atom));
        return new OperationEchoMaker<TPayload>(typedSink, coordinator, echo => _ = atom.EnqueueAsync(echo), options);
    }

    /// <summary>
    ///     Starts an echo maker that invokes the provided callback for each captured echo.
    /// </summary>
    public static OperationEchoMaker<TPayload> EnableOperationEchoing<TPayload>(
        this IOperationFinalization coordinator,
        TypedSignalSink<TPayload> typedSink,
        Action<OperationEchoEntry<TPayload>> onEcho,
        OperationEchoMakerOptions<TPayload>? options = null)
    {
        if (onEcho is null) throw new ArgumentNullException(nameof(onEcho));
        return new OperationEchoMaker<TPayload>(typedSink, coordinator, onEcho, options);
    }
}