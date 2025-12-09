# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the **Ephemeral Signals Demo** - an interactive Spectre.Console application demonstrating the Mostlylucid.Ephemeral library's signal-based coordination patterns. This is a demo/showcase project within the larger mostlylucid.ephemeral ecosystem.

The demo showcases 10 interactive scenarios plus BenchmarkDotNet integration to demonstrate:
- The three signal models (Pure Notification, Context + Hint, Command)
- Advanced patterns (Circuit Breaker, Backpressure, Metrics)
- Multi-stage pipelines and signal chains
- Real-world scenarios with rate limiting and flow control
- Performance benchmarking with memory diagnostics

## Build and Run Commands

### Interactive Demo

```bash
# Run interactive menu
dotnet run

# Run in Release mode (required for benchmarks)
dotnet run -c Release
```

### Benchmark Mode

**IMPORTANT:** Benchmarks MUST run in Release mode for accurate results.

```bash
# Run all benchmarks
dotnet run -c Release -- --benchmark all

# Run specific benchmark categories
dotnet run -c Release -- --benchmark signals
dotnet run -c Release -- --benchmark coordinators
dotnet run -c Release -- --benchmark parallelism
dotnet run -c Release -- --benchmark finale

# List available benchmarks
dotnet run -c Release -- --benchmark list
```

### Building

```bash
# Build debug
dotnet build

# Build release (for benchmarks)
dotnet build -c Release
```

## Project Architecture

### Main Components

| File | Purpose |
|------|---------|
| `Program.cs` | Interactive menu system, all 10 demo implementations, benchmark runner |
| `TestAtom.cs` | Simulated atom for demonstrations - configurable signal listeners, responses, delays |
| `ConsoleSignalLoggerAtom.cs` | Real-time signal logging to console with filtering and window management |
| `SignalBenchmarks.cs` | BenchmarkDotNet suite for performance testing with memory diagnostics |
| `ImageProcessingAtoms.cs` | Real-world image processing atoms (Load, Resize, EXIF, Watermark) |
| `ImageProcessingDemo.cs` | Multi-stage image processing pipeline demonstration |

### Demo Scenarios (Program.cs)

The demos are implemented as async methods in `Program.cs`:

1. **RunPureNotificationDemo** - File save with state queries
2. **RunContextHintDemo** - Order processing with double-safe hints
3. **RunCommandPatternDemo** - WindowSizeAtom infrastructure control
4. **RunComplexPipelineDemo** - Multi-stage rate-limited pipeline
5. **RunSignalChainDemo** - Cascading atoms (A→B→C)
6. **RunCircuitBreakerDemo** - Failure detection and recovery
7. **RunBackpressureDemo** - Queue overflow protection
8. **RunMetricsMonitoringDemo** - Real-time statistics dashboard
9. **RunDynamicRateAdjustmentDemo** - Adaptive throttling
10. **RunLiveSignalViewer** - Real-time signal visualization

### Atom Components

**TestAtom** - Demo-specific simulated atom:
- Listens to configurable signal patterns (glob matching)
- Emits response signals based on received signals
- Simulates processing with configurable delays
- Tracks state (count, last signal, history, busy flag)
- Provides query methods demonstrating the pattern

**ConsoleSignalLoggerAtom** - Observability component:
- Captures signals with include/exclude pattern filtering
- Auto-output to console with color-coded log levels
- Window size limiting and sample rate control
- `DumpWindow()` for displaying signal history
- Statistics tracking (total received, logged, filtered)

**Image Processing Atoms** - Real-world example:
- `LoadImageAtom` - Loads images with I/O tracking
- `ResizeImageAtom` - Creates multiple sized variants (thumb, medium, large)
- `ExifProcessingAtom` - Adds EXIF metadata
- `WatermarkAtom` - Adds watermarks and returns processing results

### External Atom Packages

The demo uses production atoms from the ephemeral ecosystem:

| Package | Purpose |
|---------|---------|
| `WindowSizeAtom` | Dynamic SignalSink capacity management via command signals |
| `RateLimitAtom` | Token bucket rate limiter using System.Threading.RateLimiting |

These are referenced from `../../src/mostlylucid.ephemeral.atoms.*` projects.

## Key Patterns

### The Three Signal Models

1. **Pure Notification** (Default)
   - Signal carries no data, just event name
   - State lives in atoms, listeners query for truth
   - Example: `sink.Raise("file.saved")` → listener queries `fileAtom.GetCurrentFilename()`

2. **Context + Hint** (Double-Safe)
   - Signal includes hint for optimization
   - Listeners use hint but verify with atom
   - Example: `sink.Raise("order.placed:ORD-123")` → fast-path uses hint, then verifies

3. **Command** (Exception - Infrastructure Only)
   - Signal carries imperative command
   - Used for infrastructure control, not domain events
   - Example: `sink.Raise("window.size.set:500")` → direct configuration change

### Benchmark Structure

Benchmarks use BenchmarkDotNet with:
- `[MemoryDiagnoser]` for allocation tracking
- InProcessEmitToolchain for faster iteration
- Custom config exporting to Markdown, CSV, HTML, JSON
- Descriptions for each benchmark method
- Categories: Signal infrastructure, Coordinators, Parallelism, FINALE

## Dependencies

| Package | Purpose |
|---------|---------|
| `Spectre.Console` | Rich terminal UI and interactive menus |
| `BenchmarkDotNet` | Performance benchmarking with memory diagnostics |
| `SixLabors.ImageSharp` | Image processing for realistic demo scenarios |
| `Microsoft.Extensions.Logging.Abstractions` | ILogger integration for ConsoleSignalLoggerAtom |

## Related Documentation

- [Demo README](README.md) - Full demo documentation with pattern philosophy
- [Parent CLAUDE.md](../../CLAUDE.md) - Main library architecture
- [SIGNALS_PATTERN.md](../../SIGNALS_PATTERN.md) - Signal pattern documentation
- [Parent README.md](../../README.md) - Library overview and quick start

## Testing

This is a demo project, not a test project. For unit tests, see:
- `../../tests/mostlylucid.ephemeral.tests/`
- `../../tests/mostlylucid.ephemeral.sqlite.singlewriter.tests/`

Run tests from solution root:
```bash
cd ../..
dotnet test mostlylucid.ephemeral.sln
```
