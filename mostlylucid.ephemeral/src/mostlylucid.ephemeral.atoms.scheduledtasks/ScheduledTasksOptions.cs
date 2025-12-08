using System;

namespace Mostlylucid.Ephemeral.Atoms.ScheduledTasks;

/// <summary>
/// Options for <see cref="ScheduledTasksAtom"/>.
/// </summary>
public sealed class ScheduledTasksOptions
{
    /// <summary>
    /// How often to scan schedules for due tasks. Defaults to once per second.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Clock used when evaluating cron expressions. Defaults to UTC now.
    /// </summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>
    /// Whether the background polling loop starts automatically. Set to false for manual triggering in tests.
    /// </summary>
    public bool AutoStart { get; init; } = true;
}
