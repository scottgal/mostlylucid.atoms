# Mostlylucid.Ephemeral.Complete

The complete bundle of all Mostlylucid.Ephemeral packages in one install.

## What's Included

### Core
- **mostlylucid.ephemeral** - Bounded async execution with signal coordination

### Atoms (Composable Building Blocks)
- **atoms.batching** - Time/size-based batch aggregation
- **atoms.fixedwork** - Fixed worker pools
- **atoms.keyedsequential** - Per-key sequential processing
- **atoms.retry** - Exponential backoff retry
- **atoms.signalaware** - Pause/resume on signals

### Patterns (Ready-to-Use Compositions)
- **patterns.adaptiverate** - Signal-driven rate limiting
- **patterns.anomalydetector** - Failure pattern detection
- **patterns.backpressure** - Queue depth management
- **patterns.circuitbreaker** - Failure isolation
- **patterns.controlledfanout** - Signal-gated fan-out
- **patterns.dynamicconcurrency** - Runtime concurrency adjustment
- **patterns.keyedpriorityfanout** - Priority lanes with per-key ordering
- **patterns.longwindowdemo** - Configurable window sizes
- **patterns.reactivefanout** - Event-driven fan-out
- **patterns.signalcoordinatedreads** - Read/write coordination
- **patterns.signalinghttp** - HTTP calls with signal integration
- **patterns.signallogwatcher** - Log pattern monitoring
- **patterns.signalreactionshowcase** - Signal emission demos
- **patterns.telemetry** - OpenTelemetry integration

## Installation

```bash
dotnet add package mostlylucid.ephemeral.complete
```

## License

MIT
