using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mostlylucid.Ephemeral.Atoms.ScheduledTasks;

/// <summary>
/// A small wrapper around <see cref="EphemeralWorkCoordinator{DurableTask}"/> that keeps scheduled jobs durable.
/// </summary>
public sealed class DurableTaskAtom : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<DurableTask> _coordinator;

    /// <summary>
    /// Creates a durable task atom that executes the provided handler whenever a task is dequeued.
    /// </summary>
    public DurableTaskAtom(Func<DurableTask, CancellationToken, Task> handler, EphemeralOptions? options = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        _coordinator = new EphemeralWorkCoordinator<DurableTask>(handler, options ?? CreateDefaultOptions());
    }

    /// <summary>
    /// Enqueues a durable task for execution.
    /// </summary>
    public ValueTask EnqueueAsync(DurableTask task, CancellationToken cancellationToken = default)
        => _coordinator.EnqueueAsync(task, cancellationToken);

    /// <summary>
    /// Drain outstanding tasks.
    /// </summary>
    public Task DrainAsync(CancellationToken cancellationToken = default)
        => _coordinator.DrainAsync(cancellationToken);

    /// <summary>
    /// Wait until no work is pending or active.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(25);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_coordinator.PendingCount == 0 && _coordinator.ActiveCount == 0)
                return;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Gracefully stop accepting tasks and await completion.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private static EphemeralOptions CreateDefaultOptions() =>
        new()
        {
            MaxConcurrency = 1,
            MaxTrackedOperations = 128,
            MaxOperationLifetime = TimeSpan.FromMinutes(5),
            EnableOperationEcho = false
        };
}
