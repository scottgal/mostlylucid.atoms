# Correlation Architecture in Ephemeral: Operation IDs vs Coordinator Keys

## Abstract

Traditional observability systems use span/trace IDs for correlation within a single request and separate correlation IDs for linking related operations across time. Ephemeral's signal-based architecture reveals a more fundamental pattern: **coordinators themselves are correlation domains**, with operation IDs providing intra-domain tracing and coordinator keys providing cross-temporal entity correlation. This paper explores how this two-axis correlation model enables both real-time operational debugging and long-term behavioral analysis.

---

## The Two Axes of Correlation

Every `SignalEvent` in Ephemeral carries two fundamental correlation dimensions:

```csharp
public readonly record struct SignalEvent(
    string Signal,
    long OperationId,      // Axis 1: Intra-coordinator trace
    string? Key,           // Axis 2: Cross-operation correlation
    DateTimeOffset Timestamp,
    SignalPropagation? Propagation = null);
```

### Axis 1: Operation ID (Intra-Coordinator Tracing)

**Operation IDs** are unique per operation instance within a coordinator:

- Generated once per operation via `EphemeralIdGenerator.NextId()`
- Atomic, globally unique (Interlocked counter)
- Scoped to a single execution lifetime
- Analogous to **span/trace IDs** in distributed tracing

**Purpose:** Answer "*What happened during this one execution?*"

```csharp
// Get all signals from operation 42
var opSignals = sink.GetOpSignals(42);

// Timeline for this specific operation
var timeline = opSignals.OrderBy(s => s.Timestamp);
foreach (var signal in timeline)
{
    Console.WriteLine($"{signal.Timestamp:HH:mm:ss.fff} - {signal.Signal}");
}
```

**Use cases:**
- Debug a specific failed operation
- Build flame graphs for a single execution
- Trace signal propagation within one operation
- Identify bottlenecks in a particular run

### Axis 2: Coordinator Key (Cross-Temporal Correlation)

**Coordinator keys** identify the correlation domain:

- Persistent entity identifier (SignatureId, TenantId, RequestId, UserId, etc.)
- Spans multiple operations over time
- Defines the **correlation entity** whose behavior you're tracking
- Analogous to **correlation IDs** in distributed systems

**Purpose:** Answer "*What is the behavioral pattern of this entity over time?*"

```csharp
// Create coordinator keyed by signature
var coordinator = new EphemeralKeyedWorkCoordinator<Task, string>(
    task => task.SignatureId,  // Key selector - defines correlation domain
    async (task, ct) => { /* ... */ },
    new EphemeralOptions { Signals = sink }
);

// Later: Get all signals for signature "SIG-12345" across ALL operations
var signatureSignals = sink.Sense(s => s.Key == "SIG-12345");

// Behavioral analysis across time
var errorRate = signatureSignals.Count(s => s.Signal.StartsWith("error."))
    / (double)signatureSignals.Count;
```

**Use cases:**
- Detect behavioral anomalies for a specific user/tenant
- Track signature behavior evolution over time
- Correlate operations belonging to the same business entity
- Build entity-centric dashboards and alerts

---

## Mental Model: Episodes vs Characters

The distinction becomes clear with this analogy:

| Concept | Operation ID | Coordinator Key |
|---------|-------------|-----------------|
| **Metaphor** | Episode ID | Character ID |
| **Scope** | Single execution | Entity lifetime |
| **Question** | "What happened in this run?" | "What's this entity's story?" |
| **Tracing equivalent** | Span/Trace ID | Correlation ID |
| **Time horizon** | Milliseconds to seconds | Minutes to months |
| **Cardinality** | High (millions) | Lower (thousands) |
| **Cleanup** | Evicted by window size | Persistent in analytics |

**Example:**

```csharp
// CHARACTER: Signature "MALWARE-2024-001"
//   EPISODE 1 (OpId=42): Detected in file.exe → signals: scan.started, malware.detected
//   EPISODE 2 (OpId=84): Detected in payload.dll → signals: scan.started, malware.detected
//   EPISODE 3 (OpId=105): User whitelisted → signals: whitelist.added, scan.skipped
//   EPISODE 4 (OpId=127): New variant detected → signals: scan.started, variant.match

// Query the CHARACTER's story:
var signatureHistory = sink.Sense(s => s.Key == "MALWARE-2024-001")
    .OrderBy(s => s.Timestamp);

// Query a specific EPISODE:
var episode2Details = sink.GetOpSignals(84);
```

---

## Coordinator as Correlation Domain

The crucial insight: **The coordinator itself defines the correlation boundary.**

### Pattern 1: Request-Scoped Correlation

```csharp
public class RequestHandler
{
    public async Task<IActionResult> ProcessRequest(HttpContext context)
    {
        var requestId = context.TraceIdentifier;

        // Coordinator keyed by RequestId = correlation domain
        await using var coordinator = new EphemeralKeyedWorkCoordinator<Task, string>(
            task => requestId,  // All tasks in this request share correlation key
            async (task, ct) =>
            {
                op.Emit("task.started");
                // ... work ...
                op.Emit("task.complete");
            },
            new EphemeralOptions { Signals = requestSink }
        );

        // All operations in this request:
        // - Have unique OperationIds (episodes)
        // - Share Key=requestId (character)
    }
}
```

**Correlation question:** "What happened during request X?"
- **Answer:** All signals where `Key == requestId`
- **Drill-down:** Filter by `OperationId` to see specific task execution

### Pattern 2: Tenant-Scoped Correlation

```csharp
public class TenantWorkProcessor
{
    private readonly EphemeralKeyedWorkCoordinator<Work, string> _coordinator;

    public TenantWorkProcessor(SignalSink globalSink)
    {
        _coordinator = new EphemeralKeyedWorkCoordinator<Work, string>(
            work => work.TenantId,  // Correlation domain = tenant
            async (work, ct) =>
            {
                op.Emit("work.started");
                // ... process ...
                op.Emit("work.complete");
            },
            new EphemeralOptions { Signals = globalSink }
        );
    }
}
```

**Correlation question:** "What is tenant ABC's usage pattern?"
- **Answer:** All signals where `Key == "ABC"` over the past week
- **Analysis:** Detect anomalies, track quota usage, identify abuse

### Pattern 3: Signature-Scoped Correlation

```csharp
public class SignatureCoordinator
{
    private readonly EphemeralKeyedWorkCoordinator<ScanTask, string> _coordinator;

    public SignatureCoordinator(SignalSink sink)
    {
        _coordinator = new EphemeralKeyedWorkCoordinator<ScanTask, string>(
            task => task.SignatureId,  // Correlation domain = signature
            async (task, ct) =>
            {
                op.Emit("scan.started");
                if (MatchesSignature(task))
                {
                    op.Emit("signature.match");
                    if (IsNewVariant(task))
                        op.Emit("variant.detected");
                }
                op.Emit("scan.complete");
            },
            new EphemeralOptions { Signals = sink }
        );
    }
}
```

**Correlation question:** "How is this malware signature behaving in the wild?"
- **Answer:** All signals where `Key == signatureId` across all scans
- **Insights:** Variant evolution, false positive rates, geographic distribution

---

## Hierarchical Correlation

Coordinators can nest, creating **correlation hierarchies**:

```csharp
// Level 1: Tenant correlation
var tenantCoordinator = new EphemeralKeyedWorkCoordinator<Request, string>(
    req => req.TenantId,
    async (req, ct) =>
    {
        op.Emit("tenant.request.started");

        // Level 2: Request correlation (nested)
        await using var requestCoordinator = new EphemeralKeyedWorkCoordinator<Task, string>(
            task => req.RequestId,
            async (task, ct) =>
            {
                op.Emit("request.task.started");
                // ... work ...
                op.Emit("request.task.complete");
            },
            new EphemeralOptions { Signals = sink }  // Same sink!
        );

        op.Emit("tenant.request.complete");
    },
    new EphemeralOptions { Signals = sink }
);
```

**Signals now have:**
- `OperationId` - specific task execution
- `Key` - varies by which coordinator emitted:
  - Tenant-level signals: `Key = tenantId`
  - Request-level signals: `Key = requestId`

**Query patterns:**

```csharp
// Tenant-level view
var tenantView = sink.Sense(s => s.Key == "TENANT-123");

// Request-level view
var requestView = sink.Sense(s => s.Key == "REQ-456");

// Cross-cutting: All errors for this tenant
var tenantErrors = sink.Sense(s =>
    s.Signal.StartsWith("error.") &&
    tenantIds.Contains(s.Key));  // Need to know tenant's request IDs
```

**Solution:** Use signal conventions:

```csharp
op.Emit($"tenant:{tenantId}:request.started");
op.Emit($"tenant:{tenantId}:task.started");

// Query: All activity for tenant
var tenantActivity = sink.Sense(s => s.Signal.StartsWith($"tenant:{tenantId}:"));
```

---

## Comparison to Traditional Observability

### Distributed Tracing (OpenTelemetry, Jaeger)

| Concept | Traditional Tracing | Ephemeral Signals |
|---------|---------------------|-------------------|
| **Trace ID** | Request-scoped identifier | Coordinator key (e.g., RequestId) |
| **Span ID** | Operation within trace | Operation ID |
| **Parent Span ID** | Hierarchical relationship | Signal propagation chain |
| **Attributes** | Key-value metadata | Signal name + timestamp |
| **Baggage** | Cross-service context | Shared SignalSink |
| **Sampling** | Probabilistic retention | Window-based eviction |

**Key difference:** Traces are **request-centric** (spans within a trace). Ephemeral is **entity-centric** (operations within a correlation domain).

### Application Performance Monitoring (APM)

| Concept | APM Systems | Ephemeral Signals |
|---------|-------------|-------------------|
| **Transaction** | User-facing request | Coordinator + Key |
| **Segment** | Internal operation | Operation ID |
| **Custom Metrics** | Counters, gauges | Signal counts/patterns |
| **Error Tracking** | Exception grouping | `error.*` signal patterns |
| **User Context** | User ID, session | Coordinator key |

**Key difference:** APM systems **store everything** centrally. Ephemeral uses **bounded windows** with automatic eviction.

---

## Query Patterns

### Pattern 1: Operation Timeline (Episode View)

```csharp
// "What happened during operation 42?"
var opSignals = sink.GetOpSignals(42);
var timeline = opSignals.OrderBy(s => s.Timestamp);

Console.WriteLine($"Operation {42} Timeline:");
foreach (var signal in timeline)
{
    Console.WriteLine($"  {signal.Timestamp:HH:mm:ss.fff} - {signal.Signal}");
}

// Output:
//   10:23:45.123 - scan.started
//   10:23:45.456 - signature.loaded
//   10:23:46.789 - signature.match
//   10:23:47.012 - scan.complete
```

### Pattern 2: Entity Behavior (Character View)

```csharp
// "What's the behavior of signature SIG-12345?"
var signatureSignals = sink.Sense(s => s.Key == "SIG-12345");

var grouped = signatureSignals
    .GroupBy(s => s.OperationId)  // Group episodes
    .Select(g => new
    {
        OperationId = g.Key,
        StartTime = g.Min(s => s.Timestamp),
        Signals = g.OrderBy(s => s.Timestamp).Select(s => s.Signal).ToList()
    });

Console.WriteLine($"Signature SIG-12345 History:");
foreach (var op in grouped.OrderBy(g => g.StartTime))
{
    Console.WriteLine($"  Op {op.OperationId} at {op.StartTime:HH:mm:ss}:");
    Console.WriteLine($"    {string.Join(" → ", op.Signals)}");
}

// Output:
//   Op 42 at 10:23:45:
//     scan.started → signature.match → scan.complete
//   Op 84 at 11:15:22:
//     scan.started → signature.match → variant.detected → scan.complete
//   Op 105 at 14:30:11:
//     scan.started → whitelist.check → scan.skipped
```

### Pattern 3: Cross-Entity Aggregation

```csharp
// "Which signatures have the highest error rates?"
var errorRatesBySignature = sink.Sense()
    .Where(s => s.Key != null)
    .GroupBy(s => s.Key)
    .Select(g => new
    {
        Signature = g.Key,
        TotalOperations = g.Select(s => s.OperationId).Distinct().Count(),
        ErrorOperations = g.Where(s => s.Signal.StartsWith("error."))
                           .Select(s => s.OperationId)
                           .Distinct()
                           .Count()
    })
    .Select(x => new
    {
        x.Signature,
        ErrorRate = x.ErrorOperations / (double)x.TotalOperations
    })
    .OrderByDescending(x => x.ErrorRate);

foreach (var sig in errorRatesBySignature.Take(10))
{
    Console.WriteLine($"{sig.Signature}: {sig.ErrorRate:P2} error rate");
}
```

### Pattern 4: Temporal Correlation

```csharp
// "Show me all operations for this entity in the last 5 minutes"
var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
var recentActivity = sink.Sense(s =>
    s.Key == "ENTITY-123" &&
    s.Timestamp >= cutoff);

// Group by operation and build timelines
var recentOperations = recentActivity
    .GroupBy(s => s.OperationId)
    .Select(g => new
    {
        OperationId = g.Key,
        Start = g.Min(s => s.Timestamp),
        End = g.Max(s => s.Timestamp),
        Duration = g.Max(s => s.Timestamp) - g.Min(s => s.Timestamp),
        SignalCount = g.Count(),
        Success = !g.Any(s => s.Signal.StartsWith("error."))
    });
```

---

## Implementation Patterns

### Pattern: Explicit Key Assignment

```csharp
// Coordinator sets the key domain explicitly
var coordinator = new EphemeralKeyedWorkCoordinator<Work, string>(
    work => work.EntityId,  // Key selector
    async (work, ct) =>
    {
        // All signals emitted here will have Key = work.EntityId
        op.Emit("work.started");
        // ...
    },
    new EphemeralOptions { Signals = sink }
);
```

**Signals produced:**
```
SignalEvent {
    Signal = "work.started",
    OperationId = 42,
    Key = "ENTITY-123",  // ← Automatically set by coordinator
    Timestamp = 2025-12-10T10:23:45.123Z
}
```

### Pattern: Manual Key Injection

```csharp
// For coordinators without keying, manually inject via signal name
var coordinator = new EphemeralWorkCoordinator<Work>(
    async (work, ct) =>
    {
        // Encode correlation in signal name
        op.Emit($"entity:{work.EntityId}:work.started");
        op.Emit($"entity:{work.EntityId}:work.complete");
    },
    new EphemeralOptions { Signals = sink }
);

// Query: Extract entity from signal name
var entitySignals = sink.Sense(s => s.Signal.StartsWith("entity:ENTITY-123:"));
```

### Pattern: Composite Keys

```csharp
// Multiple correlation dimensions
public record CompositeKey(string TenantId, string UserId);

var coordinator = new EphemeralKeyedWorkCoordinator<Work, CompositeKey>(
    work => new CompositeKey(work.TenantId, work.UserId),
    async (work, ct) => { /* ... */ },
    new EphemeralOptions { Signals = sink }
);

// Note: CompositeKey will be ToString()'d for Key field
// Better approach: Use structured signal names
op.Emit($"tenant:{tenantId}:user:{userId}:action.started");
```

### Pattern: Global + Entity Sinks

```csharp
// Separate sinks for different correlation scopes
var globalSink = new SignalSink(maxCapacity: 10_000);  // All signals
var entitySink = new SignalSink(maxCapacity: 1_000);   // This entity only

var coordinator = new EphemeralKeyedWorkCoordinator<Work, string>(
    work => work.EntityId,
    async (work, ct) =>
    {
        op.Emit("work.started");

        // Also emit to entity-specific sink
        entitySink.Raise($"entity:{work.EntityId}:work.started");
    },
    new EphemeralOptions { Signals = globalSink }
);

// Query global: Cross-entity analysis
var globalErrors = globalSink.Sense(s => s.Signal.StartsWith("error."));

// Query entity: Focused investigation
var entityHistory = entitySink.Sense();  // All signals for this entity
```

---

## Practical Examples

### Example 1: HTTP Request Correlation

```csharp
public class RequestProcessor
{
    private readonly SignalSink _sink;

    public async Task ProcessRequest(HttpRequest request)
    {
        var requestId = request.Headers["X-Request-ID"].ToString();

        await using var coordinator = new EphemeralKeyedWorkCoordinator<Task, string>(
            task => requestId,  // Correlation domain = request
            async (task, ct) =>
            {
                op.Emit("task.started");
                await ExecuteTask(task);
                op.Emit("task.complete");
            },
            new EphemeralOptions { Signals = _sink }
        );

        // Enqueue multiple tasks for this request
        await coordinator.EnqueueAsync(new Task { Type = "Auth" });
        await coordinator.EnqueueAsync(new Task { Type = "LoadData" });
        await coordinator.EnqueueAsync(new Task { Type = "Render" });

        coordinator.Complete();
        await coordinator.DrainAsync();

        // Get request timeline
        var requestTimeline = _sink.Sense(s => s.Key == requestId)
            .OrderBy(s => s.Timestamp);

        LogRequestTimeline(requestId, requestTimeline);
    }
}
```

### Example 2: Signature Behavior Tracking

```csharp
public class SignatureBehaviorTracker
{
    private readonly SignalSink _behaviorSink;
    private readonly EphemeralKeyedWorkCoordinator<ScanJob, string> _coordinator;

    public SignatureBehaviorTracker()
    {
        _behaviorSink = new SignalSink(
            maxCapacity: 50_000,
            maxAge: TimeSpan.FromHours(24)  // 24hr behavior window
        );

        _coordinator = new EphemeralKeyedWorkCoordinator<ScanJob, string>(
            job => job.SignatureId,  // Correlation domain = signature
            async (job, ct) =>
            {
                op.Emit("scan.started");

                var match = await ScanWithSignature(job);
                if (match.IsMatch)
                {
                    op.Emit("signature.match");

                    if (match.IsNewVariant)
                        op.Emit("variant.detected");

                    if (match.FalsePositive)
                        op.Emit("false_positive");
                }
                else
                {
                    op.Emit("no_match");
                }

                op.Emit("scan.complete");
            },
            new EphemeralOptions { Signals = _behaviorSink }
        );
    }

    public SignatureBehaviorSummary GetBehaviorSummary(string signatureId)
    {
        var signals = _behaviorSink.Sense(s => s.Key == signatureId);

        return new SignatureBehaviorSummary
        {
            SignatureId = signatureId,
            TotalScans = signals.Count(s => s.Signal == "scan.started"),
            Matches = signals.Count(s => s.Signal == "signature.match"),
            Variants = signals.Count(s => s.Signal == "variant.detected"),
            FalsePositives = signals.Count(s => s.Signal == "false_positive"),
            FirstSeen = signals.Min(s => s.Timestamp),
            LastSeen = signals.Max(s => s.Timestamp),
            DetectionRate = signals.Count(s => s.Signal == "signature.match") /
                           (double)signals.Count(s => s.Signal == "scan.started")
        };
    }
}
```

### Example 3: Multi-Tenant Anomaly Detection

```csharp
public class TenantAnomalyDetector
{
    private readonly SignalSink _tenantSink;

    public async Task DetectAnomalies(string tenantId)
    {
        // Get all signals for this tenant in the last hour
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var tenantSignals = _tenantSink.Sense(s =>
            s.Key == tenantId &&
            s.Timestamp >= cutoff);

        // Baseline: Historical error rate
        var historicalErrorRate = await GetHistoricalErrorRate(tenantId);

        // Current: Error rate in last hour
        var currentErrorRate = tenantSignals.Count(s => s.Signal.StartsWith("error.")) /
                               (double)tenantSignals.Count();

        // Detect anomaly
        if (currentErrorRate > historicalErrorRate * 3)  // 3x baseline
        {
            await RaiseAnomalyAlert(tenantId, new
            {
                HistoricalRate = historicalErrorRate,
                CurrentRate = currentErrorRate,
                Multiplier = currentErrorRate / historicalErrorRate,
                TimeWindow = "1 hour",
                SignalCount = tenantSignals.Count()
            });
        }
    }
}
```

---

## Design Principles

### Principle 1: Coordinators Define Correlation Boundaries

**Don't:** Add correlation IDs manually everywhere
```csharp
// ❌ Manual correlation tracking
op.Emit($"correlationId:{corrId}:task.started");
op.Emit($"correlationId:{corrId}:task.complete");
```

**Do:** Let coordinator key define correlation
```csharp
// ✅ Coordinator-defined correlation
var coordinator = new EphemeralKeyedWorkCoordinator<Task, string>(
    task => task.CorrelationId,  // Key = correlation domain
    async (task, ct) =>
    {
        op.Emit("task.started");  // Automatically keyed
        op.Emit("task.complete");
    },
    options
);
```

### Principle 2: Operation IDs for Episodes, Keys for Stories

**Don't:** Use operation IDs for cross-operation correlation
```csharp
// ❌ Trying to correlate different operations
var relatedOps = sink.Sense(s =>
    s.OperationId == op1 || s.OperationId == op2 || s.OperationId == op3);
```

**Do:** Use coordinator keys for correlation
```csharp
// ✅ Correlate via shared key
var entityOps = sink.Sense(s => s.Key == "ENTITY-123");
```

### Principle 3: Bounded Windows for Real-Time, Persistence for History

**Don't:** Rely on SignalSink for long-term storage
```csharp
// ❌ Sink will evict old signals
var lastYearData = sink.Sense(s =>
    s.Timestamp >= DateTimeOffset.UtcNow.AddYears(-1));  // Likely empty!
```

**Do:** Export signals for long-term analytics
```csharp
// ✅ Real-time in sink, historical in storage
sink.Subscribe(signal =>
{
    // Real-time processing
    ProcessSignal(signal);

    // Long-term storage
    _analyticsDb.InsertSignal(signal);
});

// Query historical
var yearData = await _analyticsDb.GetSignals(entityId, from: DateTime.UtcNow.AddYears(-1));
```

### Principle 4: Signal Names for Filtering, Keys for Grouping

**Don't:** Encode all context in signal names
```csharp
// ❌ Over-encoded signals
op.Emit($"tenant:ABC:user:john:request:456:task:auth:status:started");
```

**Do:** Use coordinator keys + clean signal names
```csharp
// ✅ Coordinator key provides context
var coordinator = new EphemeralKeyedWorkCoordinator<Task, string>(
    task => $"{task.TenantId}:{task.UserId}",
    async (task, ct) =>
    {
        op.Emit("auth.started");  // Clean, filterable
        op.Emit("auth.complete");
    },
    options
);
```

---

## Comparison Table

| Aspect | Operation ID | Coordinator Key |
|--------|-------------|-----------------|
| **Scope** | Single operation execution | Entity/domain over time |
| **Lifetime** | ms-seconds (operation duration) | minutes-days (entity lifetime) |
| **Uniqueness** | Globally unique (counter) | Business-meaningful identifier |
| **Cardinality** | Very high (millions) | Moderate (thousands) |
| **Query pattern** | "Show me this one execution" | "Show me this entity's behavior" |
| **Example values** | `42`, `127`, `99821` | `"TENANT-ABC"`, `"SIG-12345"`, `"REQ-456"` |
| **Storage** | Bounded window (ephemeral) | Can persist (analytics DB) |
| **Analogous to** | Span/Trace ID | Correlation ID / Session ID |
| **Primary use** | Debugging, profiling | Behavioral analysis, monitoring |

---

## Conclusion

Ephemeral's correlation architecture reveals a fundamental truth about distributed systems observability:

1. **Operation IDs** provide *local* tracing within an execution context
2. **Coordinator keys** provide *global* correlation across time and operations
3. **Coordinators themselves** are the correlation boundaries, not just execution managers

This two-axis model enables:
- **Real-time debugging** via operation timelines
- **Behavioral analysis** via entity histories
- **Hierarchical correlation** through nested coordinators
- **Bounded memory** through window-based eviction

By recognizing that **coordinators are correlation domains**, we shift from manually tracking correlation IDs everywhere to letting the infrastructure define correlation boundaries naturally. The result is cleaner code, better observability, and deeper insights into system behavior.

---

## Further Reading

- [SignalSink Lifetime and Multi-Coordinator Usage](./SignalSink-Lifetime.md)
- [Signal Propagation and Constraints](../CLAUDE.md#signal-infrastructure)
- [Coordinator Selection Patterns](../CLAUDE.md#coordinator-selection)
- [Examples: Signature Behavior Tracking](../src/mostlylucid.ephemeral.patterns.*)
