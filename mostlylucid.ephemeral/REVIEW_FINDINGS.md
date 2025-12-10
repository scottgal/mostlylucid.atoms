# Code Review Findings - Best Practice Violations

**Date**: 2025-12-10
**Scope**: demos/, src/mostlylucid.ephemeral.atoms.*, src/mostlylucid.ephemeral.patterns.*

## Summary

Review of codebase identified several areas where code does not follow the patterns documented in BEST_PRACTICES.md. Most issues are in demo code which intentionally uses simplified patterns for educational purposes, but some atoms could benefit from updates.

## Critical Issues

### None Found

No critical anti-patterns detected. Code does not violate the "Shared Sink Anti-Pattern" - coordinators are properly isolated.

## Medium Priority Issues

### 1. Demos Don't Use Scoped Signal Architecture (v1.6.8+)

**Files Affected**: `demos/mostlylucid.ephemeral.demo/Program.cs` (all demo methods)

**Issue**: Demos use flat signal names like "file.saved", "order.placed" instead of hierarchical scoped format "sink.coordinator.atom.name" introduced in v1.6.8.

**Current Code**:
```csharp
sink.Raise("file.save");
sink.Raise("order.placed:ORD-123");
sink.Raise("circuit.open");
```

**Should Be** (using ScopedSignalEmitter):
```csharp
var context = new SignalContext("demo", "pipeline", "FileAtom");
var emitter = new ScopedSignalEmitter(context, operationId, sink);
emitter.Emit("save");  // → "demo.pipeline.FileAtom.save"
```

**Impact**:
- Demos don't showcase the modern scoped architecture
- Developers learning from demos may not discover ScopedSignalEmitter
- Signal name collisions possible in larger systems

**Recommendation**:
- Update at least ONE demo (e.g., "Demo 4: Complex Multi-Step System") to showcase scoped signals
- Add comment to other demos noting they use "simplified flat signals for clarity - see Demo 4 for production-ready scoped approach"
- Keep most demos simple for educational purposes

### 2. ImageSharp Atoms Don't Use Scoped Signals

**Files Affected**: `src/mostlylucid.ephemeral.atoms.imagesharp/ImageProcessingAtoms.cs`

**Issue**: ImageSharp atoms emit flat signals:
```csharp
ctx.Emit("image.loading");
ctx.Emit("resize.started");
_signals.Raise($"resize.{sizeName}.started");
```

**Should Use**: SignalContext and ScopedSignalEmitter for proper hierarchical naming

**Impact**:
- Signal name collisions if multiple image pipelines run in same sink
- Harder to filter/query signals by scope
- Doesn't demonstrate best practices for atom development

**Recommendation**:
- Update ImageProcessingContext to hold a SignalContext
- Use ScopedSignalEmitter for all signal emissions
- Maintain backward compatibility by keeping signal patterns similar (just hierarchical)

### 3. Missing Operation ID Filtering in Demo Listeners

**Files Affected**: `demos/mostlylucid.ephemeral.demo/Program.cs` (multiple demo methods)

**Issue**: Signal listeners don't filter by operation ID:
```csharp
var notificationListener = new Action<SignalEvent>(signal =>
{
    if (signal.Signal == "file.saved")  // ❌ No operation ID check
    {
        var count = fileAtom.GetProcessedCount();
        AnsiConsole.MarkupLine($"✓ File saved! Total: {count}");
    }
});
```

**Should Be**:
```csharp
var myOperationId = 12345;
var notificationListener = new Action<SignalEvent>(signal =>
{
    if (signal.Signal == "file.saved" && signal.OperationId == myOperationId)  // ✅ Filtered
    {
        var count = fileAtom.GetProcessedCount();
        AnsiConsole.MarkupLine($"✓ File saved! Total: {count}");
    }
});
```

**Impact**:
- Demos process signals from ALL operations, not just their own
- In concurrent scenarios, listeners fire for wrong operations
- Doesn't teach developers the importance of operation ID filtering

**Recommendation**:
- Add operation ID tracking to at least one complex demo
- Add explicit comments explaining when filtering is needed vs. when it's optional
- For simple demos, add comment: "// Note: In production, filter by OperationId if concurrent"

## Low Priority Issues

### 4. Subscription Disposal Pattern Could Be Safer

**Files Affected**: `demos/mostlylucid.ephemeral.demo/Program.cs`

**Issue**: Manual subscription cleanup with `+=` and `-=`:
```csharp
sink.SignalRaised += notificationListener;
// ... work ...
sink.SignalRaised -= notificationListener;
```

**Better Pattern** (from BEST_PRACTICES.md):
```csharp
var subscription = sink.Subscribe(signal => { /* handle */ });
// ... work ...
subscription.Dispose();
```

**Impact**: Low - current code works correctly, but Subscribe() pattern is more robust

**Recommendation**:
- Update one demo to show Subscribe() pattern
- Add to best practices showcase

### 5. Demos Mix Pure Notification, Context+Hint, and Command Patterns Without Clear Separation

**Files Affected**: `demos/mostlylucid.ephemeral.demo/Program.cs`

**Issue**: While demos DO showcase all three signal models (Pure Notification, Context+Hint, Command), the separation could be clearer.

**Current**: Each demo method demonstrates ONE pattern well
**Could Improve**: Add headers/comments clearly labeling which pattern is active

**Recommendation**:
- Add more prominent comments at the start of each demo section
- Reference the SIGNALS_PATTERN.md document
- This is already mostly correct, just needs clearer signposting

## Positive Findings

✅ **No Shared Sink Anti-Pattern**: Coordinators are properly isolated
✅ **Proper Disposal**: All IAsyncDisposable resources are disposed correctly
✅ **No Blocking in Handlers**: Signal handlers are lightweight
✅ **Atoms Follow Single Responsibility**: Each atom has one clear job
✅ **Good Documentation**: Atoms have excellent XML comments
✅ **Proper Error Handling**: Try/catch blocks in appropriate places

## Atom Review - No Issues Found

Reviewed key atoms:
- `RateLimitAtom` ✅ Well-structured, proper signal handling
- `WindowSizeAtom` ✅ Excellent documentation, clean code
- `ImageProcessingAtoms` ✅ Good fluent API design

All atoms follow best practices:
- Single responsibility
- Proper disposal
- Lightweight signal handlers
- Clear options classes
- Good error handling

## Pattern Review - Not Completed

Deferred to separate review (many pattern files, out of scope for this pass).

## Recommendations Priority

1. **HIGH**: Update BEST_PRACTICES.md reference in main README files
2. **MEDIUM**: Add one demo showcasing ScopedSignalEmitter (v1.6.8 architecture)
3. **MEDIUM**: Update ImageSharp atoms to use scoped signals
4. **LOW**: Add operation ID filtering examples to complex demos
5. **LOW**: Show Subscribe() disposal pattern in one demo

## Proposed Changes

The majority of issues are in DEMO code which is intentionally simplified. Proposed approach:

1. Keep most demos simple (flat signals, no operation ID filtering) for educational clarity
2. Add ONE advanced demo ("Demo 4: Complex Pipeline" is perfect candidate) that showcases ALL best practices:
   - Scoped signals with ScopedSignalEmitter
   - Operation ID filtering
   - Subscribe() disposal pattern
   - Proper coordinator isolation
3. Add comment headers to existing demos: "// 🎓 Educational simplified pattern - see Demo 4 for production best practices"
4. Update ImageSharp atoms to use scoped architecture (breaking change, bump version)
5. Add prominent links to BEST_PRACTICES.md in README files

This approach:
- Maintains educational simplicity for beginners
- Provides advanced reference for production use
- Documents the architectural evolution (v1.6.8 scoped signals)
- Avoids breaking existing demo code that users may have copied

## Next Steps

1. Update main README to reference BEST_PRACTICES.md ← START HERE
2. Decide: Update Demo 4, or create new "Demo 14: Production Best Practices"?
3. Plan breaking change for ImageSharp atoms (v2.0?)
4. Review patterns directory (separate task)
