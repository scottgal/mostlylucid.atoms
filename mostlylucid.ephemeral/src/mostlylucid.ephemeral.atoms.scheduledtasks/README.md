# Mostlylucid.Ephemeral.Atoms.ScheduledTasks

Durable task + cron-driven helpers that keep scheduled work visible until someone handles it.

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨

## Signal Management (v2.0+)

**⚠️ Important for long-lived atoms:** ScheduledTasksAtom and DurableTaskAtom are long-lived atoms that may accumulate signals over time. As of v2.0.0, you can control signal cleanup via atom-level parameters:

```csharp
var durable = new DurableTaskAtom(
    async (task, ct) => { /* handler */ },
    maxSignalCount: 5000,                      // Limit total signals from this atom
    maxSignalAge: TimeSpan.FromMinutes(30)     // Max age for this atom's signals
);
```

This prevents unbounded signal growth in the shared SignalSink while keeping scheduled task signals visible for monitoring. See [ReleaseNotes.txt](../../ReleaseNotes.txt) for v2.0 migration details.

## DurableTaskAtom

DurableTaskAtom wraps an EphemeralWorkCoordinator<DurableTask> so every scheduled job is tracked, sampled, and pinned
until it completes. Provide a handler that raises signals, writes to storage, or notifies downstream coordinators.

`csharp
var sink = new SignalSink();
await using var durable = new DurableTaskAtom(async (task, ct) =>
{
sink.Raise(task.Signal, key: task.Key);
});

If you just want to wait for every pending task to complete (for example in tests) without shutting down the durable
atom, call `durable.WaitForIdleAsync()`. `DrainAsync` still requires `Complete()` because it waits for the coordinator
to stop accepting new work, but `WaitForIdleAsync` simply polls until `PendingCount` and `ActiveCount` hit zero so you
can enqueue more work afterwards.

Each DurableTask carries the schedule Name, Signal, optional Key, the configured Payload, and the human-readable Description. Downstream listeners treat the emitted signal as the durable record of what ran (filenames, URLs, metadata, etc.) so they can keep logging, tracing, or acknowledging the work in the same coordinator window.
`

The atom exposes EnqueueAsync to post durable work plus DrainAsync/DisposeAsync for graceful shutdown.

## ScheduledTasksAtom

ScheduledTasksAtom monitors a set of cron schedules, steams them into DurableTask instances, and enqueues them on the
provided atom. The scheduler runs on a 1-second poll interval by default, but you can override the clock, turn off the
background loop (AutoStart = false), or call TriggerAsync() manually from tests.

`jsonc
[
  {
    "name": "daily-report",
    "cron": "0 0 * * *",
    "signal": "schedule.daily",
    "key": "reports",
    "description": "Daily report pickup",
    "payload": { "tenant": "sales" },
    "runOnStartup": true
  }
]
`

`csharp
var definitions = ScheduledTaskDefinition.LoadFromJsonFile("schedules.json");
var sink = new SignalSink();
await using var durable = new DurableTaskAtom(async (task, ct) =>
{
    sink.Raise(task.Signal, key: task.Key);
});
await using var scheduler = new ScheduledTasksAtom(durable, definitions);
`

Each JSON entry is enriched into a DurableTask that carries its signal, payload, and scheduling metadata. Add imeZone (
Windows/Linux ID), ormat (e.g., "CronFormat.Standard"), or
unOnStartup to control timing, and rely on the durable atom to keep responsibilities pinned until downstream tooling or
acknowledgements release them.