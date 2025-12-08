using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mostlylucid.Ephemeral.Atoms.ScheduledTasks;

/// <summary>
/// Streams configured cron schedules into durable tasks.
/// </summary>
public sealed class ScheduledTasksAtom : IAsyncDisposable
{
    private readonly DurableTaskAtom _durableAtom;
    private readonly List<ScheduledTaskState> _states;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _loop;

    /// <summary>
    /// Creates a scheduler that enqueues durable tasks whenever a cron definition matches.
    /// </summary>
    public ScheduledTasksAtom(DurableTaskAtom durableAtom, IEnumerable<ScheduledTaskDefinition> definitions, ScheduledTasksOptions? options = null)
    {
        _durableAtom = durableAtom ?? throw new ArgumentNullException(nameof(durableAtom));
        if (definitions is null) throw new ArgumentNullException(nameof(definitions));

        var list = definitions.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one scheduled task definition is required.", nameof(definitions));

        options ??= new ScheduledTasksOptions();
        _clock = options.Clock ?? (() => DateTimeOffset.UtcNow);
        _pollInterval = options.PollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : options.PollInterval;

        _states = list.Select(def => new ScheduledTaskState(def, _clock)).ToList();

        if (options.AutoStart)
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Triggers any due tasks based on the current clock instant.
    /// Useful in tests or when AutoStart = false.
    /// </summary>
    public Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        return TriggerDueTasksAsync(_clock(), cancellationToken);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await TriggerDueTasksAsync(_clock(), cancellationToken).ConfigureAwait(false);
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TriggerDueTasksAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var state in _states)
        {
            while (true)
            {
                var next = state.NextRun;
                if (next is null || next > now)
                    break;

                await EnqueueAsync(state.Definition, next.Value, cancellationToken).ConfigureAwait(false);
                state.ScheduleNext();
            }
        }
    }

    private ValueTask EnqueueAsync(ScheduledTaskDefinition definition, DateTimeOffset scheduledAt, CancellationToken cancellationToken)
    {
        var task = new DurableTask(
            definition.Name,
            definition.Signal,
            scheduledAt,
            definition.Key,
            definition.Payload,
            definition.Description);

        return _durableAtom.EnqueueAsync(task, cancellationToken);
    }

    /// <summary>
    /// Stops the background loop (if running) and cancels future work.
    /// </summary>
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
    }

    private sealed class ScheduledTaskState
    {
        public ScheduledTaskDefinition Definition { get; }
        public DateTimeOffset? NextRun { get; private set; }

        private readonly Func<DateTimeOffset> _clock;

        public ScheduledTaskState(ScheduledTaskDefinition definition, Func<DateTimeOffset> clock)
        {
            Definition = definition;
            _clock = clock;

            var now = _clock();
            if (definition.RunOnStartup)
            {
                NextRun = now;
            }
            else
            {
                NextRun = definition.GetNextOccurrence(now, inclusive: true);
                if (NextRun is not null && NextRun <= now)
                {
                    NextRun = definition.GetNextOccurrence(now.AddTicks(1), inclusive: false);
                }
            }
        }

        public void ScheduleNext()
        {
            var reference = NextRun ?? _clock();
            NextRun = Definition.GetNextOccurrence(reference, inclusive: false);
        }
    }
}
