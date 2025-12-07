# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
dotnet build mostlylucid.ephemeral.sln
dotnet build mostlylucid.ephemeral.sln -c Release
```

## Project Overview

This is **Mostlylucid.Ephemeral** - a .NET 10 library for bounded, observable, self-cleaning async execution with signal-based coordination. The tagline is "Fire... and Don't *Quite* Forget."

The library fills the gap between fire-and-forget (`_ = Task.Run(...)`) and blocking (`await`) by providing trackable, bounded, debuggable async execution with complete observability.

## Architecture

All source lives in `mostlylucid.ephemeral/Ephemeral/`. The namespace is `Mostlylucid.Helpers.Ephemeral`.

### Core Components

| File | Purpose |
|------|---------|
| `EphemeralWorkCoordinator.cs` | Long-lived work queue coordinator - the main entry point for continuous processing |
| `EphemeralKeyedWorkCoordinator.cs` | Per-key sequential execution with optional fair scheduling |
| `EphemeralResultCoordinator.cs` | Variant that captures results alongside operation metadata |
| `ParallelEphemeral.cs` | Static extension methods (`EphemeralForEachAsync`) for one-shot parallel processing |
| `EphemeralOperation.cs` | Internal operation tracking with signal support |
| `EphemeralOptions.cs` | Configuration (concurrency, window size, lifetime, signals) |
| `Snapshots.cs` | Immutable snapshot records exposed to consumers |

### Signal Infrastructure

| File | Purpose |
|------|---------|
| `Signals.cs` | `SignalEvent`, `SignalSink`, `SignalPropagation`, constraints |
| `SignalDispatcher.cs` | Async signal routing with pattern matching |
| `StringPatternMatcher.cs` | Glob-style pattern matching (`*`, `?`) for signal filtering |

### Supporting Infrastructure

| File | Purpose |
|------|---------|
| `ConcurrencyGates.cs` | `FixedConcurrencyGate` (SemaphoreSlim) and `AdjustableConcurrencyGate` for runtime adjustment |
| `EphemeralIdGenerator.cs` | Fast XxHash64-based allocation-free ID generation |
| `DependencyInjection.cs` | DI extension methods and factory implementations |

### Atoms (Small Opinionated Wrappers)

Located in `Atoms/`:

- `FixedWorkAtom.cs` - Bounded worker pool with stats
- `KeyedSequentialAtom.cs` - Per-key ordered execution
- `SignalAwareAtom.cs` - Cancel/defer intake based on signals (circuit-breaker pattern)
- `BatchingAtom.cs` - Coalesce items by size/time into batches
- `RetryAtom.cs` - Bounded retry/backoff around work items

### Examples

Located in `Examples/`:

- `ControlledFanOut.cs` - Global + per-key gating for bursty inputs
- `ReactiveFanOutPipeline.cs` - Upstream throttle reacting to downstream signals
- `SignalingHttpClient.cs` - Fine-grained HTTP progress/stage signals
- `SignalLogWatcher.cs` - Poll signal window for pattern matches
- `SignalAnomalyDetector.cs` - Moving-window anomaly detection
- `SignalBasedCircuitBreaker.cs` - Circuit breaker using signal history
- `AdaptiveTranslationService.cs` - Adaptive concurrency based on signal feedback

## Key Patterns

### Coordinator Selection

| Scenario | Use |
|----------|-----|
| Process collection once | `items.EphemeralForEachAsync(...)` |
| Long-lived queue | `EphemeralWorkCoordinator<T>` |
| Per-entity ordering | `EphemeralKeyedWorkCoordinator<TKey, T>` |
| Capture results | `EphemeralResultCoordinator<TInput, TResult>` |

### Signal Flow

Operations emit signals via `op.Signal("name")` or `op.Emit("name")`. Signals are:
- Queryable via `coordinator.HasSignal()`, `CountSignals()`, `GetSignalsByPattern()`
- Reactive via `CancelOnSignals`/`DeferOnSignals` options
- Shareable across coordinators via `SignalSink`

### Memory Model

- Operations stored in `ConcurrentQueue<EphemeralOperation>` with bounded window
- `MaxTrackedOperations` limits window size; old operations evict automatically
- `MaxOperationLifetime` controls how long completed operations stay visible
- Pinning (`Pin(id)`) prevents eviction for important operations
