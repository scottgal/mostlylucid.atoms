namespace Mostlylucid.Ephemeral.Atoms.Echo;

/// <summary>
///     Serializes operation echoes with a lightweight coordinator so you can persist diagnostics as the window trims
///     entries.
/// </summary>
public sealed class OperationEchoAtom<TPayload> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<OperationEchoEntry<TPayload>> _coordinator;

    /// <summary>
    ///     Creates an atom that logs echoes via the provided persist callback.
    /// </summary>
    /// <param name="persist">Arbitrary caller-supplied persistence callback.</param>
    /// <param name="maxPersistDuration">
    ///     Required, no default: <paramref name="persist" /> is arbitrary caller-supplied work, so
    ///     the same "one bad item cannot destroy this coordinator" invariant applies here.
    /// </param>
    /// <param name="options">Optional coordinator options.</param>
    public OperationEchoAtom(Func<OperationEchoEntry<TPayload>, CancellationToken, Task> persist,
        TimeSpan maxPersistDuration,
        EphemeralOptions? options = null)
    {
        if (persist is null) throw new ArgumentNullException(nameof(persist));

        var coordinatorOptions = options ?? new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 64,
            MaxOperationLifetime = TimeSpan.FromSeconds(30)
        };

        _coordinator = new EphemeralWorkCoordinator<OperationEchoEntry<TPayload>>(persist, maxPersistDuration,
            coordinatorOptions);
    }

    /// <summary>
    ///     Complete and dispose of the underlying coordinator.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Queue an echo for persistence.
    /// </summary>
    public ValueTask EnqueueAsync(OperationEchoEntry<TPayload> echo, CancellationToken cancellationToken = default)
    {
        return _coordinator.EnqueueAsync(echo, cancellationToken);
    }

    /// <summary>
    ///     Flush any pending echoes.
    /// </summary>
    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.DrainAsync(cancellationToken);
    }
}