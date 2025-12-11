namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
///     Serializes operation echoes with a lightweight coordinator so you can persist diagnostics as the window trims
///     entries.
/// </summary>
public sealed class OperationEchoAtom<TPayload> : AtomBase<EphemeralWorkCoordinator<OperationEchoEntry<TPayload>>>
{
    /// <summary>
    ///     Creates an atom that logs echoes via the provided persist callback.
    /// </summary>
    public OperationEchoAtom(Func<OperationEchoEntry<TPayload>, CancellationToken, Task> persist,
        EphemeralOptions? options = null)
        : base(new EphemeralWorkCoordinator<OperationEchoEntry<TPayload>>(
            persist ?? throw new ArgumentNullException(nameof(persist)),
            options ?? new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 64,
                MaxOperationLifetime = TimeSpan.FromSeconds(30)
            }))
    {
    }

    /// <summary>
    ///     Queue an echo for persistence.
    /// </summary>
    public ValueTask EnqueueAsync(OperationEchoEntry<TPayload> echo, CancellationToken cancellationToken = default)
    {
        return Coordinator.EnqueueAsync(echo, cancellationToken);
    }

    protected override void Complete()
    {
        Coordinator.Complete();
    }

    protected override Task DrainInternalAsync(CancellationToken ct)
    {
        return Coordinator.DrainAsync(ct);
    }

    public override IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot()
    {
        return Coordinator.GetSnapshot();
    }
}