# Mostlylucid.Ephemeral.Atoms.WaveOrchestrator

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.waveorchestrator.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.waveorchestrator)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll become the first *stylo*flow release 🚨🚨🚨

Wave-based parallel orchestrator with circuit breaker, early exit, and per-wave parallelism control.

```bash
dotnet add package mostlylucid.ephemeral.atoms.waveorchestrator
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.WaveOrchestrator;

var sink = new SignalSink();

// Define workers in waves
var workers = new[]
{
    // Wave 0 - Fast operations (run in parallel)
    new WaveWorker<string, string>("FastCheck1", wave: 0, priority: 10,
        async (input, ct) => await FastValidation(input, ct)),
    new WaveWorker<string, string>("FastCheck2", wave: 0, priority: 20,
        async (input, ct) => await QuickLookup(input, ct)),

    // Wave 1 - Slower operations (only if Wave 0 doesn't early-exit)
    new WaveWorker<string, string>("SlowCheck", wave: 1, priority: 10,
        async (input, ct) => await DetailedAnalysis(input, ct)),

    // Wave 2 - Expensive AI/ML (only if really needed)
    new WaveWorker<string, string>("AI", wave: 2, priority: 10,
        async (input, ct) => await ExpensiveAI(input, ct))
};

await using var orchestrator = new WaveOrchestratorAtom<string, string>(
    workers,
    new WaveOrchestratorOptions
    {
        MaxParallelPerWave = 4,
        ParallelismPerWave = { [0] = 8, [1] = 4, [2] = 1 }, // Adaptive parallelism
        EarlyExitCondition = result => result.Contains("DEFINITIVE_ANSWER"),
        CircuitBreakerThreshold = 3
    },
    sink
);

var result = await orchestrator.ExecuteAsync("my-input");

Console.WriteLine($"Completed: {result.CompletedWorkers.Count} workers");
Console.WriteLine($"Failed: {result.FailedWorkers.Count} workers");
Console.WriteLine($"Duration: {result.TotalDurationMs}ms");
```

---

## Key Features

### 🌊 Wave-Based Execution
Execute workers in ordered waves. Within each wave, workers run in parallel. Between waves, execution is sequential.

### ⚡ Adaptive Parallelism
Configure different parallelism levels per wave:
- Wave 0 (fast): 8 parallel workers
- Wave 1 (moderate): 4 parallel workers
- Wave 2 (AI/LLM): 1 worker (expensive, sequential)

### 🚪 Early Exit
Stop processing when a condition is met. Save resources by skipping remaining waves when answer is found early.

### 🔌 Circuit Breaker
Automatically disable failing workers after threshold. Half-open retry after cooldown period.

### 📊 Full Observability
All execution emitted as signals:
- `wave.orchestrator.started`
- `wave.started:wave={N}:workers={count}`
- `worker.started:{name}`
- `worker.completed:{name}:duration={ms}`
- `worker.failed:{name}:error={message}`
- `circuit.opened:{name}:failures={count}`
- `wave.early_exit:wave={N}`

---

## All Options

```csharp
new WaveOrchestratorOptions
{
    // Total timeout for entire orchestration
    // Default: 5 seconds
    TotalTimeout = TimeSpan.FromSeconds(5),

    // Timeout per individual worker
    // Default: 2 seconds
    WorkerTimeout = TimeSpan.FromSeconds(2),

    // Delay between waves
    // Default: 50ms
    WaveInterval = TimeSpan.FromMilliseconds(50),

    // Maximum parallel workers per wave (global default)
    // Default: 4
    MaxParallelPerWave = 4,

    // Per-wave parallelism overrides
    // Key = wave number, Value = max parallel for that wave
    // Default: empty (uses MaxParallelPerWave for all waves)
    ParallelismPerWave = new Dictionary<int, int>
    {
        [0] = 8,  // Wave 0: 8 parallel
        [1] = 4,  // Wave 1: 4 parallel
        [2] = 1   // Wave 2: 1 parallel (AI/LLM)
    },

    // Consecutive failures before circuit breaker opens
    // Default: 3
    CircuitBreakerThreshold = 3,

    // Time before retrying a circuit-broken worker
    // Default: 30 seconds
    CircuitBreakerResetTime = TimeSpan.FromSeconds(30),

    // Early exit condition - stop processing if true
    // Default: null (no early exit)
    EarlyExitCondition = result => result.Confidence > 0.9
}
```

---

## Pattern: Fast-Path Bot Detection

Inspired by BotDetection's blackboard orchestrator - optimize for the 99% case while handling edge cases.

```csharp
var workers = new[]
{
    // Wave 0: Pattern matching (<1ms each)
    new WaveWorker<Request, BotResult>("UserAgent", 0, 10, CheckUserAgent),
    new WaveWorker<Request, BotResult>("Headers", 0, 20, CheckHeaders),

    // Wave 1: Lookups (1-10ms each)
    new WaveWorker<Request, BotResult>("IPReputation", 1, 10, CheckIP),
    new WaveWorker<Request, BotResult>("Behavioral", 1, 20, CheckBehavior),

    // Wave 2: Analysis (10-50ms)
    new WaveWorker<Request, BotResult>("Inconsistency", 2, 10, CrossCheck),

    // Wave 3: AI (100-500ms) - only for uncertain cases
    new WaveWorker<Request, BotResult>("LLM", 3, 10, AskAI)
};

var options = new WaveOrchestratorOptions
{
    ParallelismPerWave = { [0] = 8, [1] = 4, [2] = 2, [3] = 1 },
    EarlyExitCondition = r => r.Confidence > 0.95 || r.Confidence < 0.05,
    TotalTimeout = TimeSpan.FromMilliseconds(150)
};

// Most requests exit at Wave 0 (definitive bot or definitive human)
// Uncertain requests proceed to later waves for deeper analysis
```

---

## Pattern: Multi-Stage Data Pipeline

```csharp
var workers = new[]
{
    // Wave 0: Load and validate
    new WaveWorker<string, ProcessedData>("Load", 0, 10, LoadData),
    new WaveWorker<string, ProcessedData>("Validate", 0, 20, ValidateSchema),

    // Wave 1: Transform (parallel for different transformations)
    new WaveWorker<string, ProcessedData>("Transform1", 1, 10, Transform1),
    new WaveWorker<string, ProcessedData>("Transform2", 1, 10, Transform2),
    new WaveWorker<string, ProcessedData>("Transform3", 1, 10, Transform3),

    // Wave 2: Aggregate results
    new WaveWorker<string, ProcessedData>("Aggregate", 2, 10, AggregateResults),

    // Wave 3: Save
    new WaveWorker<string, ProcessedData>("Save", 3, 10, SaveToDatabase)
};
```

---

## Use Cases

### API Request Processing
Fast-path validation → slow-path deep analysis → expensive AI classification

### Machine Learning Pipelines
Data loading → parallel feature extraction → model inference → aggregation

### E-Commerce Fraud Detection
Quick checks (card BIN, IP) → behavioral analysis → ML model scoring

### Content Moderation
Fast keyword scan → image analysis → LLM-based nuanced review

### Multi-Cloud Deployment
Try primary cloud → failover to secondary → tertiary fallback with circuit breakers

---

## Performance

**Wave Execution Overhead**: ~50-100µs per wave transition
**Circuit Breaker Check**: <1µs per worker
**Signal Emission**: ~1.2µs per signal (790K+ signals/sec)

**Typical Bot Detection Pipeline:**
- Wave 0 (pattern matching): 0.5ms (8 parallel)
- Wave 1 (lookups): 5ms (4 parallel) - skipped 80% of time via early exit
- Wave 2 (analysis): 25ms (2 parallel) - skipped 95% of time
- Wave 3 (AI): 200ms (1 worker) - skipped 99% of time

**Result**: 99% of requests complete in <1ms, 1% that need deep analysis take 25-200ms

---

## Related Packages

| Package                                                                                                                       | Description             |
|-------------------------------------------------------------------------------------------------------------------------------|-------------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                 | Core library            |
| [mostlylucid.ephemeral.atoms.priorityprocessor](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.priorityprocessor) | Priority-based failover |
| [mostlylucid.ephemeral.atoms.retry](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry)                         | Retry with backoff      |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Circuit breaker         |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                               | All in one DLL          |

## License

Unlicense (public domain)
