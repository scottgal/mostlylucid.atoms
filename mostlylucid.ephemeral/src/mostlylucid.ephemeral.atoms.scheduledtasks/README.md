# Mostlylucid.Ephemeral.Atoms.ScheduledTasks

Durable task + cron-driven helpers that keep scheduled work visible until someone handles it.

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
