# Mostlylucid.Ephemeral

Bounded async execution with signal-based coordination. Small, composable building blocks for concurrent work.

## Core Concept

Operations flow through coordinators with bounded concurrency. Signals provide cross-cutting observability without coupling.

```csharp
await using var coordinator = new EphemeralWorkCoordinator<Request>(
    async (req, ct) => await ProcessAsync(req, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

await coordinator.EnqueueAsync(request);
```

## Packages

### Core
```bash
dotnet add package mostlylucid.ephemeral
```

### Atoms (Composable Building Blocks)
| Package | Description |
|---------|-------------|
| `mostlylucid.ephemeral.atoms.fixedwork` | Fixed-concurrency work pipelines |
| `mostlylucid.ephemeral.atoms.keyedsequential` | Per-key sequential processing |
| `mostlylucid.ephemeral.atoms.signalaware` | Pause/cancel on signals |
| `mostlylucid.ephemeral.atoms.batching` | Time/size-based batching |
| `mostlylucid.ephemeral.atoms.retry` | Exponential backoff retry |

### Patterns (Ready-to-Use Compositions)
| Package | Description |
|---------|-------------|
| `mostlylucid.ephemeral.patterns.circuitbreaker` | Stateless circuit breaker |
| `mostlylucid.ephemeral.patterns.backpressure` | Signal-driven backpressure |
| `mostlylucid.ephemeral.patterns.controlledfanout` | Bounded fan-out with per-key ordering |
| `mostlylucid.ephemeral.patterns.dynamicconcurrency` | Runtime concurrency adjustment |
| `mostlylucid.ephemeral.patterns.adaptiverate` | Signal-driven rate limiting |
| `mostlylucid.ephemeral.patterns.telemetry` | OpenTelemetry integration |
| `mostlylucid.ephemeral.patterns.keyedpriorityfanout` | Priority lanes with per-key ordering |
| `mostlylucid.ephemeral.patterns.signalcoordinatedreads` | Read/write coordination |
| + 6 more pattern packages | |

### Demo Package
```bash
dotnet add package mostlylucid.ephemeral.sqlite.singlewriter
```
Demonstrates ephemeral patterns: single-writer coordination, sampling, self-focusing LRU cache.

### Complete Bundle
```bash
dotnet add package mostlylucid.ephemeral.complete
```
All packages in one install.

## Quick Examples

### Per-Key Sequential Processing
```csharp
await using var coordinator = new EphemeralKeyedWorkCoordinator<Order, string>(
    order => order.CustomerId,
    async (order, ct) => await ProcessOrder(order, ct),
    new EphemeralOptions { MaxConcurrency = 16, MaxConcurrencyPerKey = 1 });

// Orders for same customer processed in order
await coordinator.EnqueueAsync(order1);
await coordinator.EnqueueAsync(order2);
```

### Signal-Based Circuit Breaker
```csharp
var breaker = new SignalBasedCircuitBreaker(
    failureSignal: "api.failure",
    threshold: 5,
    windowSize: TimeSpan.FromSeconds(30));

if (breaker.IsOpen(coordinator))
    throw new CircuitOpenException("Too many failures");
```

### Backpressure
```csharp
var coordinator = SignalDrivenBackpressure.Create<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    maxConcurrency: 4);

sink.Raise("backpressure.downstream");  // New work auto-defers
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Your Application                      │
├─────────────────────────────────────────────────────────┤
│  Patterns (ready-to-use)     │  Atoms (building blocks) │
│  - CircuitBreaker            │  - FixedWork             │
│  - Backpressure              │  - KeyedSequential       │
│  - ControlledFanOut          │  - SignalAware           │
│  - ...                       │  - Batching, Retry       │
├─────────────────────────────────────────────────────────┤
│                 mostlylucid.ephemeral (core)            │
│  - EphemeralWorkCoordinator                             │
│  - EphemeralKeyedWorkCoordinator                        │
│  - SignalSink, SignalDispatcher                         │
│  - PriorityWorkCoordinator                              │
└─────────────────────────────────────────────────────────┘
```

## Key Features

- **Bounded execution** - MaxConcurrency, MaxTrackedOperations
- **Per-key ordering** - Process items with same key sequentially
- **Signal coordination** - Cancel, defer, or react to signals
- **Priority lanes** - Multiple queues with different priorities
- **Sampling** - Configurable observability without overhead
- **Zero external dependencies** - Core package is dependency-free

## License

MIT
