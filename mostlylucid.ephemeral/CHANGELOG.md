# Changelog

All notable changes to Mostlylucid.Ephemeral will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.0.0] - 2026-08-21

### Breaking Changes

- **`maxBodyDuration` is now a required constructor parameter** across every coordinator
  (`EphemeralWorkCoordinator`, `EphemeralKeyedWorkCoordinator`, `EphemeralResultCoordinator`,
  `PriorityWorkCoordinator`/`PriorityKeyedWorkCoordinator`) and every atom/pattern built on them
  (~40 call sites across the family, including the full taxonomy atom set).
  - Rationale: a body whose `Task` never resolves previously held its concurrency slot forever —
    silently, permanently. No exception, no log, just a coordinator that stops draining. Confirmed as
    the cause of a real ~20-hour production wedge.
  - Not optional-with-a-default: an unbounded default reproduces the defect for anyone who doesn't
    opt in; a "safe" baked-in default silently changes behavior for every existing caller on upgrade.
  - See [BREAKING_CHANGES_3.0.md](BREAKING_CHANGES_3.0.md) for the full migration guide, including a
    **positional-argument hazard** worth reading before you patch call sites — adding a bare
    positional `TimeSpan` next to existing `TimeSpan` parameters can silently bind to the wrong one.

### Added
- **`BodyDurationGuard`**: the shared, zero-allocation bounded-execution mechanism behind the above.
  Trips loudly — signal `"coordinator.body.timeout"`, a typed `BodyDurationExceededException`, and a
  freed concurrency slot — never silently.
- **`BackoffStrategies`** (`Mostlylucid.Ephemeral.Atoms.Retry`): named, reusable backoff functions —
  `Constant`, `Linear`, `Exponential`, `ExponentialWithJitter` — replacing the previous baked-in
  `50ms * attempt` default in `RetryAtom`.
- **`RetryAtom` signal emission**: now actually raises `AttemptFailedSignal`, `ExhaustedSignal`, and
  `SucceededAfterRetrySignal` onto its `SignalSink` — previously accepted but never used.
- **`RetryAtom` circuit breaker composition**: optional `SignalBasedCircuitBreaker` gates
  `EnqueueAsync`, throwing `CircuitOpenException` while open.
- **`SignalBasedCircuitBreaker` `SignalSink`-based overloads**: `IsOpen`, `IsOpenMatching`,
  `GetFailureCount`, `GetTimeUntilClose` now also accept a raw `SignalSink`, not just a coordinator —
  needed because atoms like `RetryAtom` have no access to the `EphemeralOperation` object and can only
  raise signals on the sink directly.

### Fixed
- **`SignalAwareAtom.EnqueueAsync`**: previously only checked a locally-seeded ambient signal set
  (via manual `Raise()`), never the live shared `SignalSink` — a real cancel-on signal raised the
  normal way never blocked intake here. Now checks both.
- **Polly removed** from `Mostlylucid.Notify` (licensing). Was an unused `PackageReference` — zero
  `using Polly` anywhere in the codebase — so no code-level impact.

### Testing
- New coverage for `BodyDurationGuard` (hung-body trip across all three base coordinators),
  `RetryAtom` (signal emission, backoff math, breaker gating), and `SignalAwareAtom` (the sink-fix
  above) — `RetryAtom`, `SignalAwareAtom`, and `SignalBasedCircuitBreaker` had no test coverage at all
  before this release despite being published packages.
- Full solution: 300+ tests, 0 failures, across all three target frameworks (net8/9/10).

## [2.9.1] - 2026-07-09

Additive, non-breaking. One new optional hook on `SlidingCacheAtom`.

### Added
- **`SlidingCacheAtom<TKey,TResult>` eviction hook — `onEvict`.** New optional ctor parameter
  `Func<TKey, TResult, CancellationToken, ValueTask>? onEvict = null`, the mirror of `factory`:
  `factory` computes a value at birth, `onEvict` receives `(key, value)` at death. It fires once
  per entry when the entry leaves the cache — expiry sweep (`cache.evict.expired`) or
  size-pressure eviction (`cache.evict.cold`) — and for every remaining live entry on
  `DisposeAsync` (graceful shutdown flush). This is the ONE point at which an evicted value is
  still reachable: the cache is the source of truth and eviction destroys it, so a signal
  listener can't query for the value after the fact (per `SIGNALS_PATTERN.md`, eviction signals
  stay key-only notifications — the callback is the loss-free durable-handoff channel). Enables
  escalate-on-eviction (persist a summary of a dying entry) without the cache knowing anything
  about persistence.
  - Callbacks run **after** the internal cleanup lock is released, so a slow callback
    back-pressures eviction (it is awaited) without wedging the cache.
  - Per-callback exceptions are isolated: swallowed and surfaced as a
    `cache.evict.callback.error:<key>:<ExceptionType>` signal, so one failed hand-off never
    stops the rest of an eviction batch.
  - **Not** fired on explicit `Invalidate` / `Clear` (deliberate cache management, not entry
    death). The existing key-only eviction signals are unchanged.
  - Backward compatible: defaults to `null` (no behavior change for existing callers) and is
    appended at the end of the ctor, so every positional/named call site keeps compiling.

## [2.9.0] - 2026-07-08

Family-wide version alignment: every `Mostlylucid.Ephemeral.*` package now ships at a
single version per tag, with self-consistent inter-package dependencies. There are **no
breaking API changes since 2.6.4** — see [BREAKING_CHANGES_2.6.4_to_2.9.0.md](BREAKING_CHANGES_2.6.4_to_2.9.0.md).

### Changed
- **Split `Mostlylucid.Ephemeral.Atoms.Llm`** into three packages, keeping the original
  package ID as a source-compatible meta (no consumer code changes required):
  - `Mostlylucid.Ephemeral.Atoms.Llm.Abstractions` — provider-agnostic contracts
    (`IEphemeralPrompter` / `IEphemeralPicker` / `IEphemeralLlmInvoker` /
    `IEphemeralWriteback` + `EphemeralPrompt`). Zero dependencies, multi-targets
    net8.0/net9.0/net10.0 so any consumer can implement the contracts.
  - `Mostlylucid.Ephemeral.Atoms.Llm.Coordinator` — the runtime (`EphemeralLlmCoordinator`
    + `AddEphemeralLlmCoordinator` DI), net10.0, depends on `Mostlylucid.Common`.
  - `Mostlylucid.Ephemeral.Atoms.Llm` — meta-package referencing both.
- **`mostlylucid.ephemeral.complete`** now composes the family via project references and
  always pins its dependencies to the release version. Previously its `InternalPackageVersion`
  was stuck at `2.3.0` and two references (`Atoms.Volatile`, `Patterns.Telemetry`) were
  hardcoded to `2.3.0-alpha1`.

### Fixed
- **`mostlylucid.ephemeral.attributes`** and **`mostlylucid.ephemeral.logging`** were
  stranded at `2.6.4` because the tag-triggered publish workflow never packed them. They now
  ride every tag with the rest of the family.

### Security
- Pinned the **SQLitePCLRaw** stack to the patched 3.x releases (`bundle_e_sqlite3` /
  `core` / `provider.e_sqlite3` 3.0.3, `lib.e_sqlite3` 3.53.3), resolving
  [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (HIGH), which was
  pulled transitively via `Microsoft.Data.Sqlite`. Central transitive pinning is now enabled.

### Dependencies
- Bumped to latest stable: `Microsoft.Extensions.*` / `System.*` 10.0.9, `Npgsql` 10.0.3,
  `MailKit` 4.17.0, `Cronos` 0.13.0, `Spectre.Console` 0.57.2, `YamlDotNet` 18.1.0,
  `Microsoft.NET.Test.Sdk` 18.7.0, `coverlet.collector` 10.0.1, `Microsoft.SourceLink.GitHub`
  10.0.300.
- Held `SixLabors.ImageSharp` at `3.1.12` and `SixLabors.ImageSharp.Drawing` at `2.1.7` —
  4.0 / 3.0 require a paid Six Labors license, incompatible with this FOSS package. These are
  the newest free-licensed releases and carry no known vulnerabilities.

## [2.8.1] - 2026-07-06

### Fixed
- Publish workflow path corrected for the renamed `Mostlylucid.Ephemeral.Atoms.Llm` package.

## [2.8.0] - 2026-07-06

### Changed
- **Renamed `Mostlylucid.Atoms.Ephemeral` → `Mostlylucid.Ephemeral.Atoms.Llm`** to fit the
  family naming convention (`Mostlylucid.Ephemeral.Atoms.*`). The old package ID is retired
  at 2.7.0; consumers should move to the new ID.

### Added
- `Raise(...)` helper overloads on `DetectorAtomBase` (Taxonomy) that auto-prepend the atom
  `Name` to emitted signals.

## [2.7.0] - 2026-07-04

### Added
- **`Mostlylucid.Atoms.Ephemeral`**: an ephemeral LLM-coordinator atom. A
  `prompt → pick → invoke → writeback` pipeline (`EphemeralLlmCoordinator` plus
  `IEphemeralPrompter` / `IEphemeralPicker` / `IEphemeralLlmInvoker` /
  `IEphemeralWriteback`) that ticks on an `IScheduleCoordinator`. Register via
  `AddEphemeralLlmCoordinator(...)`. Depends only on `Mostlylucid.Common`
  scheduling and the `Microsoft.Extensions` abstractions (no dependency on the
  ephemeral core).

## [2.0.0] - 2026-01-08

### Breaking Changes

- **REMOVED: `SignalSink.SignalRaised` event**
  - The event-based subscription pattern has been removed
  - Use `Subscribe()` method instead, which returns `IDisposable` for clean unsubscription
  - See [BREAKING_CHANGES_2.0.md](BREAKING_CHANGES_2.0.md) for migration guide
  - Rationale: Better memory management, guaranteed cleanup, no race conditions

- **REMOVED: .NET 6.0 and .NET 7.0 support**
  - Minimum supported frameworks: .NET 8.0, .NET 9.0, .NET 10.0
  - .NET 6.0 and 7.0 are out of Microsoft support
  - This allows use of modern C# features (`static abstract`, `required`)

### Migration from 1.x

```csharp
// Before (1.x) - event pattern
sink.SignalRaised += MyHandler;

// After (2.0) - subscription pattern
var subscription = sink.Subscribe(MyHandler);
// ... later
subscription.Dispose(); // Clean unsubscription

// Or use 'using' for scoped subscriptions
using var sub = sink.Subscribe(signal => ProcessSignal(signal));
```

### Added
- **Detection Ledger System** (`Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger`): Core evidence accumulation for detection systems
  - `DetectionLedger` - Accumulates detector contributions, aggregates with sigmoid function, produces verdict
  - `DetectionContribution` - Represents a single detector's evidence with factory methods:
    - `Bot()` - Positive confidence delta (bot-indicating)
    - `Human()` - Negative confidence delta (human-indicating)
    - `Info()` - Neutral/informational (zero delta)
    - `VerifiedBot()` - Triggers early exit for confirmed bad bots
    - `VerifiedGoodBot()` - Early exit for allowed bots (e.g., Googlebot)
  - `CategoryScore` - Breakdown by category with `TotalWeight` for explainability
  - `LearningRecord` - High-confidence records for heuristic training
  - `IEntityLedger` - Generic interface for any entity type (images, documents, requests)
  - `LedgerSignal` - Individual signal with salience and provenance metadata
  - Sigmoid-based aggregation produces bot probability (0.0-1.0) with confidence score
  - High-salience signals can be escalated to RAG storage or learning systems

- **Lock-free Signal Subscription**: `sink.Subscribe(handler)` returns `IDisposable`
  - Preferred over legacy `SignalRaised` event
  - Uses lock-free concurrent collections internally
  - Pattern-based forwarding: `errorSink.SubscribeToPattern(mainSink, "error.*")`

- **WindowSizeAtom**: New atom for dynamic SignalSink capacity and retention management via signals
  - Commands: `window.size.set/increase/decrease` and `window.time.set/increase/decrease`
  - Support for multiple time formats: seconds (`30s`), milliseconds (`500ms`), TimeSpan (`00:05:00`)
  - Automatic value clamping to configured min/max limits
  - Comprehensive test coverage (24 tests)
  - Full XML documentation and README with usage examples

- **SignalCommandMatch improvements**:
  - Added comprehensive XML documentation with examples
  - Added bounds checking to prevent `ArgumentOutOfRangeException` on malformed signals
  - 18 new unit tests covering edge cases and security scenarios

- **SignalSink performance improvements**:
  - Added bounded cleanup iteration (max 1000 per cycle) to prevent performance degradation
  - 13 new performance and stress tests
  - High-volume stress test validating 10k+ concurrent signal handling

### Changed
- **Dependency updates (SECURITY)**:
  - `System.Text.Json`: Updated to framework-appropriate secure versions
    - NET 6.0/7.0: `8.0.5` (fixes GHSA-8g4q-xg66-9fp4, GHSA-hh2w-p6rv-4g7w)
    - NET 8.0: `8.0.5`
    - NET 9.0: `9.0.1`
    - NET 10.0: `10.0.0`
  - `Npgsql`: Updated to secure versions
    - NET 6.0/7.0/8.0: `8.0.5` (fixes GHSA-x9vc-6hfv-hg8c)
    - NET 9.0: `9.0.2`
    - NET 10.0: `10.0.0`
  - `Microsoft.Data.Sqlite`: Updated to latest framework-specific versions
    - NET 6.0: `6.0.36` + System.Text.Json `8.0.5`
    - NET 7.0: `7.0.20` + System.Text.Json `8.0.5`
    - NET 8.0: `8.0.11` + System.Text.Json `8.0.5`
    - NET 9.0: `9.0.1` + System.Text.Json `9.0.1`
    - NET 10.0: `10.0.0` + System.Text.Json `10.0.0`

### Fixed
- **SignalCommandMatch parsing** (SECURITY):
  - Fixed potential buffer overflow when parsing signals with edge-case boundaries
  - Added explicit bounds check before string slicing: `if (payloadStart > signal.Length) return false;`
  - Impact: Prevents `ArgumentOutOfRangeException` on malformed input

- **SignalSink cleanup performance** (PERFORMANCE):
  - Fixed unbounded loop in `Cleanup()` method that could cause performance degradation
  - Added iteration limits (max 1000 items per cleanup cycle)
  - Fixed race condition between `TryPeek` and `TryDequeue` operations
  - Impact: Prevents performance degradation under high signal load (5000+ signals)

- **WindowSizeAtom time parsing** (BUG):
  - Fixed millisecond format parsing: "500ms" was incorrectly parsed as "500m" + "s" = 5 seconds
  - Reordered format checks to test "ms" before "s"
  - Added explanatory comment: "Check 'ms' before 's' to avoid matching '500ms' as '500m' + 's'"
  - Impact: Correct millisecond-precision signal window adjustments

### Security
- **CVE fixes**: Patched 3 high-severity vulnerabilities in System.Text.Json and Npgsql
- **Buffer overflow prevention**: Added bounds checking to SignalCommandMatch parsing
- **Input validation**: All user-controlled signal strings now validated before processing

### Performance
- **Cleanup optimization**: Bounded iteration prevents O(n) cleanup under extreme load
- **Memory efficiency**: Maintained zero-allocation design in hot paths
- **Concurrency**: Stress-tested with 10k concurrent operations - no degradation

### Testing
- **New test coverage**: Added 67 new tests (175 total, up from 108)
  - SignalCommandMatch: 18 tests (edge cases, security, parameterized)
  - WindowSizeAtom: 24 tests (functionality, concurrency, clamping)
  - SignalSink: 13 new tests (cleanup, performance, stress)
- **Test pass rate**: 100% (175/175 passing)

### Documentation
- **Added SIGNALS_PATTERN.md** - Comprehensive guide to the Ephemeral Signals pattern
  - "Hey, look at me!" philosophy explained
  - Three models: Pure Notification, Context + Hint (double-safe), Command
  - Extensive examples showing atom state management
  - Decision trees for choosing the right model
  - Anti-patterns and red flags
  - When to use hints vs pure notifications

- Added comprehensive XML documentation to:
  - `WindowSizeAtom` class (40+ lines with examples and use cases)
  - Note: WindowSizeAtom is a deliberate exception (command pattern)
  - `SignalCommandMatch` struct (70+ lines with pattern matching guide)
  - `WindowSizeAtomOptions` class (full property documentation)

- Created detailed README for WindowSizeAtom package (300+ lines)
  - Includes warning about command pattern exception
  - Contrasts with normal signal usage (notification)

- Added inline comments explaining security and performance fixes
- Updated CLAUDE.md with WindowSizeAtom documentation

## [1.0.0] - Previous Release

(Existing functionality - see git history)

---

## Security Advisories

### GHSA-8g4q-xg66-9fp4 (System.Text.Json)
**Severity**: High
**Fixed in**: System.Text.Json 8.0.5+
**Impact**: Potential denial of service
**Resolution**: Updated all projects to use patched versions

### GHSA-hh2w-p6rv-4g7w (System.Text.Json)
**Severity**: High
**Fixed in**: System.Text.Json 8.0.5+
**Impact**: Potential information disclosure
**Resolution**: Updated all projects to use patched versions

### GHSA-x9vc-6hfv-hg8c (Npgsql)
**Severity**: High
**Fixed in**: Npgsql 8.0.5+ / 9.0.2+
**Impact**: Potential SQL injection vector
**Resolution**: Updated to framework-appropriate secure versions

---

## Performance Benchmarks

### SignalSink.Cleanup() - Before vs After

| Scenario | Before | After | Improvement |
|----------|--------|-------|-------------|
| 5000 signals, capacity 10 | ~5000 iterations | ~1000 iterations | **80% reduction** |
| High-load cleanup | Unbounded | Bounded (1ms max) | **Predictable latency** |
| Concurrent stress (10k ops) | Potential hang | < 2s completion | **100% reliable** |

### Memory Impact

| Component | Before | After | Change |
|-----------|--------|-------|--------|
| WindowSizeAtom | N/A | ~200 bytes | New feature |
| SignalCommandMatch | 0 allocs | 0 allocs | ✅ No change |
| SignalSink cleanup | Variable | Bounded | ✅ Predictable |

---

## Migration Guide

### From 1.x to 2.0.0

**Breaking changes** - see [BREAKING_CHANGES_2.0.md](BREAKING_CHANGES_2.0.md) for full details.

#### SignalRaised Event Removal

```csharp
// Before (1.x)
sink.SignalRaised += (signal) => Console.WriteLine(signal.Name);

// After (2.0)
using var sub = sink.Subscribe(signal => Console.WriteLine(signal.Name));
```

#### Target Framework Updates

Update your project file:
```xml
<!-- Before -->
<TargetFrameworks>net6.0;net7.0;net8.0</TargetFrameworks>

<!-- After -->
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

#### To Use New WindowSizeAtom:

```bash
dotnet add package Mostlylucid.Ephemeral.Atoms.WindowSize
```

```csharp
// Before: Manual SignalSink updates
var sink = new SignalSink(maxCapacity: 100);
sink.UpdateWindowSize(maxCapacity: 500); // Direct API call

// After: Signal-driven updates
await using var atom = new WindowSizeAtom(sink);
sink.Raise("window.size.set:500"); // Via signals
```

#### Dependency Updates:

No action required - package restore will automatically pull patched versions.

To verify security updates:
```bash
dotnet list package --vulnerable
# Should show: No vulnerable packages found
```

---

## Contributors

- Code review, security fixes, and performance optimizations
- 67 new tests added
- Comprehensive documentation improvements

## Links

- [GitHub Repository](https://github.com/scottgal/mostlylucid.atoms)
- [Issue Tracker](https://github.com/scottgal/mostlylucid.atoms/issues)
- [Security Policy](https://github.com/scottgal/mostlylucid.atoms/security/policy)
