# Ephemeral Best Practices

> 📚 Essential patterns and conventions for building robust signal-based systems with Mostlylucid.Ephemeral

## Table of Contents

- [Signal Architecture](#signal-architecture)
- [Coordinator Patterns](#coordinator-patterns)
- [Atom Development](#atom-development)
- [Performance Optimization](#performance-optimization)
- [Testing](#testing)
- [Common Pitfalls](#common-pitfalls)

---

## Signal Architecture

### Three-Level Scoping (1.6.8+)

**Always use the three-level hierarchy**: `Sink → Coordinator → Atom`

```csharp
// ✅ GOOD: Scoped signal with full context
var context = new SignalContext(
    Sink: "request",           // Top-level boundary
    Coordinator: "gateway",    // Processing unit
    Atom: "ResizeImageJob"     // Individual operation
);
var emitter = new ScopedSignalEmitter(context, operationId, sink);
emitter.Emit("completed");  // → "request.gateway.ResizeImageJob.completed"
```

**Key principles:**
- **Sink**: Represents the top-level system boundary (e.g., "request", "background", "telemetry")
- **Coordinator**: Represents the processing unit within that boundary
- **Atom**: Represents the individual operation or job

### Signal Naming Conventions

**Use hierarchical dot notation for signal names:**

```csharp
// ✅ GOOD: Clear hierarchy and semantic meaning
sink.Raise("http.request.started");
sink.Raise("http.request.headers.parsed");
sink.Raise("http.request.body.validated");
sink.Raise("http.response.headers.written");
sink.Raise("http.response.complete");

// ❌ BAD: Flat naming loses context
sink.Raise("started");
sink.Raise("parsed");
sink.Raise("done");
```

**Convention patterns:**
- `{domain}.{entity}.{action}` - Standard pattern
- `{domain}.{entity}.{component}.{action}` - For complex hierarchies
- Use past tense for completed actions: `.completed`, `.failed`, `.validated`
- Use present continuous for ongoing: `.processing`, `.waiting`

### Signal Models: When to Use Each

#### 1. Pure Notification (Default - Recommended)

**Use when**: State lives in atoms, signals are just notifications

```csharp
// ✅ GOOD: Pure notification
sink.Raise("file.saved");

// Query atom for truth
var filename = fileAtom.GetCurrentFilename();
var size = fileAtom.GetFileSize();
```

**Advantages:**
- No coupling between emitter and listener
- Source of truth is always the atom
- Natural consistency model

#### 2. Context + Hint (Double-Safe)

**Use when**: Need optimization but want safety

```csharp
// ✅ GOOD: Hint in signal, verify with atom
sink.Raise($"order.placed:{orderId}");

// Listener uses hint as fast-path
if (orderAtom.VerifyOrderExists(orderId)) {
    // Process using hint
}
```

**Advantages:**
- Fast-path optimization
- Still maintains safety
- Good for high-throughput scenarios

#### 3. Command (Exception - Infrastructure Only)

**Use when**: Direct infrastructure control needed

```csharp
// ✅ ACCEPTABLE: Infrastructure command
sink.Raise("window.size.set:500");

// ❌ BAD: Domain logic via command
sink.Raise("order.create:user123,item456");  // NO! Use atoms
```

**Restrictions:**
- **Only** for infrastructure (window size, rate limits, circuit breakers)
- **Never** for domain logic
- **Never** for data transfer

### Signal Value Storage

**Store values in the `Key` property, not the signal name:**

```csharp
// ✅ GOOD: Value in Key property
sink.Raise("request.risk", risk.ToString());
sink.Raise("response.status", statusCode.ToString());
sink.Raise("ip.datacenter", datacenterName);

// Retrieve with type safety
var risk = GetSignal<double>("request.risk") ?? 0.0;
var status = GetSignal<int>("response.status") ?? 0;
var dc = GetSignal<string>("ip.datacenter");

// Helper method for type-safe extraction
T GetSignal<T>(string signalName, T defaultValue = default) {
    var events = sink.Sense(evt => evt.Signal == signalName);
    var latest = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();
    if (latest == default || latest.Key == null) return defaultValue;

    try {
        if (typeof(T) == typeof(string)) return (T)(object)latest.Key;
        if (typeof(T) == typeof(double))
            return double.TryParse(latest.Key, out var d) ? (T)(object)d : defaultValue;
        if (typeof(T) == typeof(int))
            return int.TryParse(latest.Key, out var i) ? (T)(object)i : defaultValue;
        if (typeof(T) == typeof(bool))
            return bool.TryParse(latest.Key, out var b) ? (T)(object)b : defaultValue;
        return defaultValue;
    }
    catch {
        return defaultValue;
    }
}
```

### Pattern Matching

**Use pattern matchers for dynamic signal extraction:**

```csharp
// ✅ GOOD: Pattern-based extraction
var patterns = new SignalPatternMatcher(new[] {
    new SignalPattern("request.risk", "risk"),
    new SignalPattern("request.honeypot", "honeypot"),
    new SignalPattern("request.ip.*", "ip_info")
});

var signals = patterns.ExtractFrom(sink);
var risk = signals.TryGetValue("risk", out var r) && r is double d ? d : 0.0;

// ✅ GOOD: Prefix pattern matching
var requestSignals = sink.Sense(evt => evt.Signal.StartsWith("request."));
var errorSignals = sink.Sense(evt => evt.Signal.StartsWith("error."));
```

---

## Coordinator Patterns

### Shared Sink Anti-Pattern

**NEVER share a SignalSink directly between coordinators:**

```csharp
// ❌ BAD: Shared sink causes signal pollution
var sharedSink = new SignalSink();
var coord1 = new EphemeralWorkCoordinator<Job>(
    async (job, ct) => { /* work */ },
    new EphemeralOptions { Signals = sharedSink }
);
var coord2 = new EphemeralWorkCoordinator<Task>(
    async (task, ct) => { /* work */ },
    new EphemeralOptions { Signals = sharedSink }  // ❌ Pollution!
);

// ✅ GOOD: Separate sinks with scoped signals
var coord1Sink = new SignalSink();
var coord2Sink = new SignalSink();

var coord1 = new EphemeralWorkCoordinator<Job>(
    async (job, ct) => { /* work */ },
    new EphemeralOptions { Signals = coord1Sink }
);

var coord2 = new EphemeralWorkCoordinator<Task>(
    async (task, ct) => { /* work */ },
    new EphemeralOptions { Signals = coord2Sink }
);

// Subscribe to other coordinator's signals if needed
coord1Sink.Subscribe(evt => {
    if (evt.Signal == "job.completed") {
        // Trigger coord2 based on coord1 signal
    }
});
```

### Coordinator Lifetime

**Coordinators should be long-lived, atoms should be operation-scoped:**

```csharp
// ✅ GOOD: Long-lived coordinator, operation-scoped atoms
public class RequestProcessor : IHostedService
{
    private EphemeralWorkCoordinator<HttpRequest> _coordinator;

    public Task StartAsync(CancellationToken ct)
    {
        _coordinator = new EphemeralWorkCoordinator<HttpRequest>(
            async (request, ct) => await ProcessRequestAsync(request, ct),
            new EphemeralOptions {
                MaxConcurrency = 100,
                Signals = _requestSink
            }
        );
        return Task.CompletedTask;
    }

    private async Task ProcessRequestAsync(HttpRequest request, CancellationToken ct)
    {
        // ✅ GOOD: Create atoms for this operation
        var analysisAtom = new RequestAnalysisAtom(request, _operationSink);
        var responseAtom = new ResponseBuilderAtom(request, _operationSink);

        await analysisAtom.AnalyzeAsync(ct);
        await responseAtom.BuildAsync(ct);

        // Atoms dispose when operation completes
    }
}
```

### Signal Window Management

**Configure appropriate window sizes based on usage:**

```csharp
// ✅ GOOD: Size window based on expected load
var highThroughputSink = new SignalSink(
    maxCapacity: 10000,      // Large window for high throughput
    maxAge: TimeSpan.FromMinutes(5)
);

var auditSink = new SignalSink(
    maxCapacity: 1000,       // Smaller window for audit
    maxAge: TimeSpan.FromHours(1)  // Longer retention
);

// ✅ GOOD: Dynamic adjustment via WindowSizeAtom
var windowAtom = new WindowSizeAtom(sink);
sink.Subscribe(evt => {
    if (evt.Signal == "load.high") {
        windowAtom.AdjustCapacity(20000);  // Scale up
    }
});
```

---

## Atom Development

### Atom Lifecycle

**Atoms should follow clear lifecycle patterns:**

```csharp
// ✅ GOOD: Well-structured atom
public sealed class ImageProcessingAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly long _operationId;
    private readonly ScopedSignalEmitter _emitter;
    private readonly IDisposable _subscription;

    public ImageProcessingAtom(
        SignalContext context,
        long operationId,
        SignalSink sink)
    {
        _sink = sink;
        _operationId = operationId;
        _emitter = new ScopedSignalEmitter(context, operationId, sink);

        // ✅ GOOD: Subscribe in constructor
        _subscription = sink.Subscribe(OnSignal);
    }

    private void OnSignal(SignalEvent evt)
    {
        // ✅ GOOD: Filter to own operation
        if (evt.OperationId != _operationId) return;

        if (evt.Signal == "image.load.complete")
        {
            _ = ProcessImageAsync();  // Fire and don't quite forget
        }
    }

    public async Task ProcessAsync(CancellationToken ct)
    {
        try
        {
            _emitter.Emit("processing.started");

            // Do work...

            _emitter.Emit("processing.completed");
        }
        catch (Exception ex)
        {
            _emitter.Emit($"processing.failed:{ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // ✅ GOOD: Cleanup subscriptions
        _subscription?.Dispose();
        _emitter.Emit("disposed");
    }
}
```

### Atom Composition

**Compose atoms for complex workflows:**

```csharp
// ✅ GOOD: Atom composition
public sealed class ImagePipelineAtom : IAsyncDisposable
{
    private readonly LoadImageAtom _loader;
    private readonly ResizeImageAtom _resizer;
    private readonly WatermarkAtom _watermark;

    public ImagePipelineAtom(SignalContext context, SignalSink sink)
    {
        var loadCtx = context with { Atom = "LoadImage" };
        var resizeCtx = context with { Atom = "ResizeImage" };
        var watermarkCtx = context with { Atom = "Watermark" };

        _loader = new LoadImageAtom(loadCtx, sink);
        _resizer = new ResizeImageAtom(resizeCtx, sink);
        _watermark = new WatermarkAtom(watermarkCtx, sink);

        // ✅ GOOD: Chain atoms via signals
        sink.Subscribe(evt => {
            if (evt.Signal.EndsWith("LoadImage.completed"))
                _ = _resizer.ProcessAsync();
            else if (evt.Signal.EndsWith("ResizeImage.completed"))
                _ = _watermark.ProcessAsync();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _loader.DisposeAsync();
        await _resizer.DisposeAsync();
        await _watermark.DisposeAsync();
    }
}
```

---

## Performance Optimization

### Hot Path Optimization

**Optimize frequently-called code paths:**

```csharp
// ✅ GOOD: Zero-allocation signal creation
var context = new SignalContext("request", "gateway", "Job123");
var key = ScopedSignalKey.ForAtom(context, "completed");

// ✅ GOOD: Reuse contexts
private readonly SignalContext _baseContext =
    new SignalContext("request", "gateway", "*");

public void EmitForOperation(string atomId, string signal)
{
    var context = _baseContext with { Atom = atomId };
    var emitter = new ScopedSignalEmitter(context, _operationId, _sink);
    emitter.Emit(signal);
}

// ✅ GOOD: Batch signal processing
public void ProcessBatch(IEnumerable<SignalEvent> signals)
{
    // Process in batches to reduce overhead
    foreach (var batch in signals.Chunk(100))
    {
        ProcessSignalBatch(batch);
    }
}
```

### Memory Management

**Manage memory efficiently:**

```csharp
// ✅ GOOD: Pin important operations
coordinator.Pin(criticalOperationId);

// ✅ GOOD: Configure appropriate lifetimes
var options = new EphemeralOptions
{
    MaxTrackedOperations = 1000,  // Limit window size
    MaxOperationLifetime = TimeSpan.FromMinutes(5),  // Auto-evict old ops
    Signals = sink
};

// ✅ GOOD: Manual cleanup when needed
coordinator.EvictBefore(DateTimeOffset.UtcNow.AddMinutes(-10));
```

### Span-Based Parsing

**Use span-based operations for performance:**

```csharp
// ✅ GOOD: Span-based pattern matching
public bool MatchesPattern(ReadOnlySpan<char> signal, ReadOnlySpan<char> pattern)
{
    if (pattern.IndexOf('*') < 0)
        return signal.SequenceEqual(pattern);

    // Wildcard matching with spans
    var prefix = pattern.Slice(0, pattern.IndexOf('*'));
    return signal.StartsWith(prefix);
}

// ✅ GOOD: Parse without allocation
public static bool TryParseScopedKey(string signal, out ScopedSignalKey key)
{
    ReadOnlySpan<char> span = signal.AsSpan();

    int firstDot = span.IndexOf('.');
    if (firstDot < 0) { key = default; return false; }

    // Continue span-based parsing...
}
```

---

## Testing

### Unit Testing Atoms

**Test atoms in isolation:**

```csharp
[Fact]
public async Task Atom_EmitsCorrectSignals()
{
    // Arrange
    var sink = new SignalSink();
    var context = new SignalContext("test", "unit", "MyAtom");
    var atom = new MyAtom(context, 1, sink);

    // Act
    await atom.ProcessAsync(CancellationToken.None);

    // Assert
    var signals = sink.Sense();
    Assert.Contains(signals, s => s.Signal == "test.unit.MyAtom.started");
    Assert.Contains(signals, s => s.Signal == "test.unit.MyAtom.completed");
}
```

### Integration Testing

**Test coordinator + atom interactions:**

```csharp
[Fact]
public async Task Pipeline_ProcessesInCorrectOrder()
{
    // Arrange
    var sink = new SignalSink();
    var coordinator = new EphemeralWorkCoordinator<Job>(
        async (job, ct) => await ProcessJobAsync(job, sink, ct),
        new EphemeralOptions { Signals = sink, MaxConcurrency = 1 }
    );

    var completedJobs = new List<string>();
    sink.Subscribe(evt => {
        if (evt.Signal.EndsWith(".completed"))
            completedJobs.Add(evt.Signal);
    });

    // Act
    await coordinator.EnqueueAsync(new Job("1"));
    await coordinator.EnqueueAsync(new Job("2"));
    coordinator.Complete();
    await coordinator.DrainAsync();

    // Assert
    Assert.Equal(2, completedJobs.Count);
    Assert.All(completedJobs, s => Assert.Contains(".completed", s));
}
```

---

## Common Pitfalls

### ❌ PITFALL 1: Forgetting Operation ID Filtering

```csharp
// ❌ BAD: Receives ALL signals
sink.Subscribe(evt => {
    if (evt.Signal == "completed") {
        // This triggers for ALL operations!
    }
});

// ✅ GOOD: Filter by operation ID
sink.Subscribe(evt => {
    if (evt.OperationId == _myOperationId && evt.Signal == "completed") {
        // Only triggers for my operation
    }
});
```

### ❌ PITFALL 2: Signal Name Collisions

```csharp
// ❌ BAD: Generic names cause collisions
sink.Raise("started");
sink.Raise("completed");

// ✅ GOOD: Scoped names prevent collisions
var emitter = new ScopedSignalEmitter(context, opId, sink);
emitter.Emit("started");  // → "request.gateway.JobX.started"
```

### ❌ PITFALL 3: Not Disposing Subscriptions

```csharp
// ❌ BAD: Subscription leak
public class MyAtom
{
    public MyAtom(SignalSink sink)
    {
        sink.Subscribe(OnSignal);  // Never disposed!
    }
}

// ✅ GOOD: Dispose subscription
public class MyAtom : IDisposable
{
    private readonly IDisposable _subscription;

    public MyAtom(SignalSink sink)
    {
        _subscription = sink.Subscribe(OnSignal);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
```

### ❌ PITFALL 4: Blocking in Signal Handlers

```csharp
// ❌ BAD: Blocking in handler
sink.Subscribe(evt => {
    Thread.Sleep(1000);  // Blocks all signal processing!
    ProcessSignal(evt).Wait();  // Deadlock risk!
});

// ✅ GOOD: Fire and don't quite forget
sink.Subscribe(evt => {
    _ = ProcessSignalAsync(evt);  // Fire async, continue
});
```

### ❌ PITFALL 5: Over-Engineering Atoms

```csharp
// ❌ BAD: Atom doing too much
public class MegaAtom
{
    public async Task DoEverything() {
        await LoadData();
        await ProcessData();
        await TransformData();
        await ValidateData();
        await SaveData();
    }
}

// ✅ GOOD: Small, focused atoms
public class LoadDataAtom { /* Just loads */ }
public class ProcessDataAtom { /* Just processes */ }
public class SaveDataAtom { /* Just saves */ }

// Compose via signals
```

---

## Quick Reference

### Signal Checklist

- [ ] Using three-level scoping (Sink → Coordinator → Atom)?
- [ ] Signal names follow hierarchical dot notation?
- [ ] Using Pure Notification model (default)?
- [ ] Values stored in Key property, not signal name?
- [ ] Operation ID filtering in subscriptions?

### Coordinator Checklist

- [ ] Each coordinator has its own SignalSink?
- [ ] Window size appropriate for load?
- [ ] Lifetime configuration matches use case?
- [ ] Coordinators are long-lived?
- [ ] Proper disposal in place?

### Atom Checklist

- [ ] Atom is operation-scoped?
- [ ] Subscriptions disposed properly?
- [ ] Signals filtered by operation ID?
- [ ] Using ScopedSignalEmitter?
- [ ] Async operations use fire-and-forget?

---

## Further Reading

- [SIGNALS_PATTERN.md](SIGNALS_PATTERN.md) - Detailed signal pattern documentation
- [README.md](README.md) - Library overview and examples
- [demos/](demos/) - Working examples of all patterns
- [ARCHITECTURE.md](ARCHITECTURE.md) - System architecture details

---

**Remember**: The goal is observable, bounded, debuggable async execution. Keep it simple, use signals for coordination, and let the library handle the complexity.
