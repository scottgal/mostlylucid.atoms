namespace Mostlylucid.Ephemeral.Atoms.ScheduledTasks;

/// <summary>
///     A small wrapper around <see cref="EphemeralWorkCoordinator{DurableTask}" /> that keeps scheduled jobs durable.
/// </summary>
public sealed class DurableTaskAtom : AtomBase<EphemeralWorkCoordinator<DurableTask>>
{
    /// <summary>
    ///     Creates a durable task atom that executes the provided handler whenever a task is dequeued.
    /// </summary>
    public DurableTaskAtom(Func<DurableTask, CancellationToken, Task> handler, EphemeralOptions? options = null)
        : base(new EphemeralWorkCoordinator<DurableTask>(
            handler ?? throw new ArgumentNullException(nameof(handler)),
            options ?? CreateDefaultOptions()))
    {
    }

    /// <summary>
    ///     Enqueues a durable task for execution.
    /// </summary>
    public ValueTask EnqueueAsync(DurableTask task, CancellationToken cancellationToken = default)
    {
        return Coordinator.EnqueueAsync(task, cancellationToken);
    }

    /// <summary>
    ///     Wait until no work is pending or active.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(25);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Coordinator.PendingCount == 0 && Coordinator.ActiveCount == 0)
                return;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    private static EphemeralOptions CreateDefaultOptions()
    {
        return new EphemeralOptions
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 128,
            MaxOperationLifetime = TimeSpan.FromMinutes(5),
            EnableOperationEcho = false
        };
    }
}