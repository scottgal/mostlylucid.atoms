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
    public OperationEchoAtom(Func<OperationEchoEntry<TPayload>, CancellationToken, Task> persist,
        EphemeralOptions? options = null)
    {
        if (persist is null) throw new ArgumentNullException(nameof(persist));

        var coordinatorOptions = options ?? new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 64,
            MaxOperationLifetime = TimeSpan.FromSeconds(30)
        };

        _coordinator = new EphemeralWorkCoordinator<OperationEchoEntry<TPayload>>(persist, coordinatorOptions);
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