# Breaking Changes in Ephemeral 3.0

This document describes breaking changes in version 3.0 and provides migration guidance.

## Why this release exists

Every coordinator in this library (`EphemeralWorkCoordinator`, `EphemeralKeyedWorkCoordinator`,
`EphemeralResultCoordinator`, and the `Priority*` wrappers built on them) invoked the caller's body
with a cancellation token but **no per-item timeout**. If a body's `Task` never resolves — a hung
downstream call, a database connection that never times out, anything that just never completes —
the coordinator's `finally` block never runs. The concurrency slot that item was holding stays held
forever. Nothing throws, nothing logs, nothing crashes. The coordinator simply stops draining new
work, silently, permanently, for the rest of the process's life.

This is not a hypothetical. It produced a real production incident: a dashboard materializer wedged
for roughly 20 hours across two independent environments, invisible to a thread-stack dump (a stuck
`await` on a semaphore that will never signal doesn't occupy an OS thread — there is nothing for a
stack walk to show).

## Breaking: `maxBodyDuration` is now a required constructor parameter

### What changed

Every coordinator constructor now takes a required `TimeSpan maxBodyDuration` parameter — no default
value. The body invocation is bounded via `Task.WaitAsync` against an injectable `TimeProvider`. On
trip: the coordinator raises signal `"coordinator.body.timeout"`, sets a
`BodyDurationExceededException` on the operation, counts it as failed, and — critically — frees the
concurrency slot so the coordinator keeps working instead of dying silently.

Affected constructors, directly:

- `EphemeralWorkCoordinator<T>`
- `EphemeralKeyedWorkCoordinator<T, TKey>`
- `EphemeralResultCoordinator<TInput, TResult>`
- `PriorityWorkCoordinator<T>` / `PriorityKeyedWorkCoordinator<T, TKey>` (via their `Options` records)

And every atom/pattern built on top of them across the family — `RetryAtom`, `SignalAwareAtom`,
`FixedWorkAtom`, `KeyedSequentialAtom`, `EscalatorAtom`, the taxonomy atom family, `SlidingCacheAtom`,
`DurableTaskAtom`, `OperationEchoAtom`, `PersistentSignalWindow`, `ControlledFanOut`,
`DynamicConcurrencyDemo`, `AdaptiveRateService`, `KeyedPriorityFanOut`, `ReactiveFanOutPipeline`, the
DI extension methods, and more. If your project references any package in this family, this build
break is not a bug — see below for how to fix it.

### Why required, not optional

Two easier options were rejected on purpose:

- **Optional with an unbounded default** (i.e. omit the check unless you opt in) leaves the defect in
  place for everyone who doesn't know to opt in — which is everyone who hit this before it had a name.
- **Optional with a "safe" baked-in default** (e.g. defaulting to 30 seconds) silently changes the
  behavior of every existing call site the moment you upgrade. A body that legitimately takes 45
  seconds today would start failing in production with no code change and no warning at compile time.

A required parameter forces a deliberate value at every call site. That's the point — the bound only
does its job if someone actually thought about it, even briefly.

### How to choose a value

There is no library-supplied default, on purpose — the right number depends entirely on what your
body actually does. As a starting rule of thumb: **take your body's observed p99 duration under real
load and multiply by 3–5x** for headroom, rather than guessing. This is offered as guidance, not
baked into the library — your telemetry, your call.

### Before (2.x)

```csharp
var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });
```

### After (3.0)

```csharp
var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    maxBodyDuration: TimeSpan.FromSeconds(30), // pick from YOUR body's p99, not this number
    new EphemeralOptions { MaxConcurrency = 8 });
```

## ⚠️ The positional-argument hazard — read this before you patch call sites

`maxBodyDuration` was inserted as a new parameter into constructors that, in several cases, already
had other `TimeSpan`-typed positional parameters after the body (e.g. `slidingExpiration`,
`absoluteExpiration`, `flushInterval`). If you fix a build break by adding a bare positional
`TimeSpan` argument without checking parameter order, **it can silently bind to the wrong parameter**.
This compiles. It runs. Your tests may even pass, because the value you meant for
`slidingExpiration` is now sitting in `maxBodyDuration` and vice versa — a plausible-looking duration
in the wrong slot rarely trips an assertion, it just quietly changes what your code actually does.

We hit this ourselves while doing this exact migration across ~40 internal call sites: several
existing tests used pure positional `TimeSpan` arguments, and a couple of them shifted meaning
silently — no compiler error, wrong behavior. We only caught it by manually auditing every call site
that had more than one positional argument after the body, not by trusting a clean build.

**When you add `maxBodyDuration` to an existing call site, use a named argument:**

```csharp
// Risky: relies on remembering the new parameter order
new SlidingCacheAtom<string, string>(factory, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

// Safe: explicit, and immune to future parameter reordering
new SlidingCacheAtom<string, string>(
    factory,
    maxFactoryDuration: TimeSpan.FromSeconds(30),
    slidingExpiration: TimeSpan.FromMinutes(5));
```

Do not treat "it compiled" as evidence the migration is correct wherever a constructor already had
other `TimeSpan` parameters. Check argument names, not just argument count.

## Non-breaking, but worth knowing about

### `RetryAtom` now actually emits signals

Previously `RetryAtom` accepted a `SignalSink` but never raised anything into it — retries were
invisible to anything watching the sink, including a `SignalBasedCircuitBreaker` pointed at the same
window. It now raises `RetryAtom<T>.AttemptFailedSignal`, `.ExhaustedSignal`, and
`.SucceededAfterRetrySignal`. If you were relying on the previous (silent) behavior for some reason,
you'll now see these signals; nothing about the retry mechanics themselves changed.

### `RetryAtom.backoff` is now required, with named strategies available

The previous baked-in default backoff (`50ms * attempt`) is gone — `backoff` is now a required
`Func<int, TimeSpan>`. Use the new `BackoffStrategies` class (`Constant`, `Linear`, `Exponential`,
`ExponentialWithJitter`) instead of writing your own lambda, or keep your existing lambda if you had
one — those still work unchanged.

### `SignalBasedCircuitBreaker` gained `SignalSink`-based overloads

`IsOpen`, `IsOpenMatching`, `GetFailureCount`, and `GetTimeUntilClose` now have overloads accepting a
raw `SignalSink` in addition to the existing `EphemeralWorkCoordinator<T>` overloads. Purely additive.

### Polly removed

`Mostlylucid.Notify`'s `PackageReference` to Polly has been removed. It was unused — zero `using
Polly` anywhere in the codebase — so this has no code-level impact. Removed for licensing reasons.

## Migration Steps

1. **Find every call site** the compiler flags after upgrading — the compiler will find them all for
   you; this is a required-parameter break, not a silent one, *except* for the positional-shift trap
   described above, which the compiler cannot catch.
2. **For each site, decide `maxBodyDuration` deliberately.** Don't paste a placeholder and move on —
   that reproduces the "picked without thinking" problem this release exists to fix. Use your own
   telemetry; 3–5x observed p99 is a starting point, not a rule.
3. **Use named arguments**, especially anywhere the constructor already had other `TimeSpan`
   parameters. See the hazard section above.
4. **Re-run your test suite** and specifically look for any test whose assertions changed meaning
   silently — a positional shift won't show up as a compile error or even necessarily a test failure.

## Need Help?

If you encounter issues migrating to 3.0:
1. Check the [GitHub Issues](https://github.com/scottgal/mostlylucid.atoms/issues)
2. Review the [CHANGELOG.md](CHANGELOG.md) for detailed changes
3. See [SIGNALS_PATTERN.md](SIGNALS_PATTERN.md) for signal pattern best practices
