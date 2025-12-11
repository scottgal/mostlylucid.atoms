# Atoms: Building Blocks for Signal-Driven Architecture

## What are Atoms?

**Atoms** are small, self-contained, composable units of functionality in the Ephemeral ecosystem. They are the fundamental building blocks of signal-driven applications.

### Definition

> An **Atom** is a focused component that:
> 1. **Does one thing well** (single responsibility)
> 2. **Communicates via signals** (emits signals when things happen)
> 3. **Reacts to signals** (subscribes to signals from other atoms)
> 4. **Is composable** (atoms work together without tight coupling)
> 5. **Is bounded** (has resource limits and self-cleaning behavior)

Atoms can be:
- **Coordinators** - Manage concurrent work (EphemeralWorkCoordinator, PriorityWorkCoordinator)
- **Data Stores** - Manage state with signal notifications (DecayingReputationWindow, OperationEchoStore)
- **Processors** - Transform data and emit results (EntropyAnalysisAtom, WaveOrchestratorAtom)
- **Orchestrators** - Coordinate other atoms (DependencyCoordinator, SignalWaveExecutor)
- **Utilities** - Provide infrastructure (RateLimitAtom, WindowSizeAtom)

---

## Philosophy: The Atom Pattern

### Traditional Approach (Tight Coupling)
```csharp
// ❌ Services directly call each other
public class OrderService
{
    private readonly InventoryService _inventory;
    private readonly PaymentService _payment;
    private readonly EmailService _email;

    public async Task ProcessOrder(Order order)
    {
        await _inventory.Reserve(order.Items);      // Direct call
        await _payment.Charge(order.Total);         // Direct call
        await _email.SendConfirmation(order);       // Direct call
    }
}
// Problem: OrderService must know about all downstream services
```

### Atom Approach (Signal-Based Decoupling)
```csharp
// ✅ Atoms communicate via signals
public class OrderProcessorAtom
{
    private readonly SignalSink _signals;

    public async Task ProcessOrder(Order order)
    {
        // Do your work
        var validation = await ValidateOrder(order);

        // Emit signals - don't call other services directly
        _signals.Raise("order.validated", order.Id);
        _signals.Raise("inventory.reserve_request", order.Id);
        _signals.Raise("payment.charge_request", order.Id);
    }
}

public class InventoryAtom
{
    public InventoryAtom(SignalSink signals)
    {
        // React to signals from other atoms
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "inventory.reserve_request")
            {
                var orderId = signal.Key;
                ReserveInventory(orderId);
                signals.Raise("inventory.reserved", orderId);
            }
        });
    }
}
// Benefit: Atoms don't know about each other, only signals
```

---

## Core Principles

### 1. Single Responsibility
Each atom does **one thing well**. Don't create "god atoms" that do everything.

**Good Examples:**
- `RateLimitAtom` - Only does rate limiting
- `EntropyAnalysisAtom` - Only analyzes entropy
- `EmailSenderAtom` - Only sends emails

**Bad Example:**
```csharp
// ❌ Anti-pattern: Atom doing too much
public class MegaAtom
{
    public async Task Process(Request request)
    {
        ValidateRequest(request);
        CheckRateLimit(request);
        AnalyzeForBots(request);
        SendEmail(request);
        UpdateDatabase(request);
        GenerateReport(request);
    }
}
// Problem: Hard to test, hard to reuse, does too many things
```

### 2. Signal-Driven Communication
Atoms **emit signals** when important events occur and **subscribe to signals** to react to events.

```csharp
public class BotDetectorAtom
{
    private readonly SignalSink _signals;

    public BotDetectorAtom(SignalSink signals)
    {
        _signals = signals;

        // React to signals from other atoms
        signals.Subscribe(signal =>
        {
            if (signal.Signal.StartsWith("request.incoming"))
            {
                AnalyzeRequest(signal.Key);
            }
        });
    }

    private void AnalyzeRequest(string requestId)
    {
        var botScore = CalculateBotScore(requestId);

        // Emit signals for others to react to
        _signals.Raise("bot.score", requestId, botScore);

        if (botScore > 0.9)
            _signals.Raise("bot.confirmed", requestId);
    }
}
```

### 3. Composability
Atoms should work together without knowing about each other. Compose complex behavior from simple atoms.

```csharp
// Build a bot detection pipeline from multiple atoms
var signals = new SignalSink();

var ipValidator = new IpValidatorAtom(signals);
var entropyAnalyzer = new EntropyAnalysisAtom(signals);
var reputationTracker = new ReputationAtom(signals);
var finalDecision = new BotDecisionAtom(signals);

// These atoms automatically coordinate via signals
// No explicit wiring needed!
```

### 4. Bounded Resources
Atoms should have **configurable limits** and **self-cleaning behavior**.

```csharp
var options = new EphemeralOptions
{
    MaxTrackedOperations = 1000,        // Bound memory usage
    MaxOperationLifetime = TimeSpan.FromMinutes(5),  // Auto-cleanup
    MaxConcurrency = 10                  // Bound parallelism
};
```

### 5. Observability
Atoms should emit signals that enable **monitoring and debugging**.

```csharp
// Atoms emit lifecycle signals
signals.Raise("atom.started", atomName);
signals.Raise("atom.processing", atomName, itemCount);
signals.Raise("atom.completed", atomName, duration);
signals.Raise("atom.failed", atomName, error);
```

---

## Anatomy of an Atom

### Minimal Atom Structure

```csharp
using Mostlylucid.Ephemeral;

namespace MyApp.Atoms;

/// <summary>
/// Simple atom that processes items and emits signals.
/// </summary>
public class MyProcessorAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly EphemeralWorkCoordinator<Item> _coordinator;
    private readonly IDisposable _subscription;

    public MyProcessorAtom(SignalSink signals, MyProcessorOptions options)
    {
        _signals = signals;

        // 1. Create internal coordinator for work management
        _coordinator = new EphemeralWorkCoordinator<Item>(
            ProcessItemAsync,
            new EphemeralOptions
            {
                MaxConcurrency = options.MaxConcurrency,
                MaxTrackedOperations = options.MaxTracked,
                Signals = signals  // Connect to signal infrastructure
            });

        // 2. Subscribe to signals from other atoms
        _subscription = signals.Subscribe(OnSignalReceived);

        // 3. Announce atom is ready
        _signals.Raise("atom.started", "MyProcessorAtom");
    }

    private void OnSignalReceived(SignalEvent signal)
    {
        // React to signals from other atoms
        if (signal.Signal.StartsWith("input.ready"))
        {
            var item = FetchItem(signal.Key);
            _coordinator.EnqueueAsync(item).GetAwaiter().GetResult();
        }
    }

    private async Task ProcessItemAsync(Item item, CancellationToken ct)
    {
        // Do the work
        var result = await DoWorkAsync(item, ct);

        // Emit signals about what happened
        _signals.Raise("item.processed", item.Id);

        if (result.Success)
            _signals.Raise("processing.success", item.Id);
        else
            _signals.Raise("processing.failed", item.Id);
    }

    private async Task<Result> DoWorkAsync(Item item, CancellationToken ct)
    {
        // Your actual work here
        await Task.Delay(100, ct);
        return new Result { Success = true };
    }

    public async ValueTask DisposeAsync()
    {
        _signals.Raise("atom.disposing", "MyProcessorAtom");
        _subscription.Dispose();
        await _coordinator.DisposeAsync();
    }
}

public class MyProcessorOptions
{
    public int MaxConcurrency { get; init; } = 4;
    public int MaxTracked { get; init; } = 1000;
}
```

---

## Types of Atoms

### 1. Coordinator Atoms
**Purpose:** Manage concurrent work execution

**Examples:**
- `EphemeralWorkCoordinator<T>` - Parallel work processing
- `EphemeralKeyedWorkCoordinator<TKey, T>` - Per-key sequential processing
- `PriorityWorkCoordinator<T>` - Multi-lane priority queues
- `DependencyCoordinator<T>` - Topological execution

**When to use:** You need to process multiple items concurrently with bounds

```csharp
public class ImageProcessorAtom
{
    private readonly EphemeralWorkCoordinator<ImageJob> _coordinator;

    public ImageProcessorAtom(SignalSink signals)
    {
        _coordinator = new EphemeralWorkCoordinator<ImageJob>(
            ProcessImageAsync,
            new EphemeralOptions
            {
                MaxConcurrency = 4,
                Signals = signals
            });
    }

    public async Task EnqueueImage(ImageJob job)
    {
        await _coordinator.EnqueueAsync(job);
    }
}
```

### 2. Data Store Atoms
**Purpose:** Manage state with signal notifications

**Examples:**
- `DecayingReputationWindow<T>` - Time-decaying scores
- `OperationEchoStore` - Recently evicted operation history
- `SignalAggregationWindow<T>` - Time-windowed signal aggregation

**When to use:** You need stateful tracking with automatic cleanup

```csharp
public class IpReputationAtom
{
    private readonly DecayingReputationWindow<string> _reputation;

    public IpReputationAtom(SignalSink signals)
    {
        _reputation = new DecayingReputationWindow<string>(
            halfLife: TimeSpan.FromMinutes(5),
            maxSize: 10000,
            signals: signals);

        // React to bad behavior signals
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "request.failed")
                _reputation.Update(signal.Key, -1.0);  // Penalize
        });
    }

    public double GetReputation(string ip) => _reputation.GetScore(ip);
}
```

### 3. Processor Atoms
**Purpose:** Transform inputs to outputs with signal emission

**Examples:**
- `EntropyAnalysisAtom<T>` - Shannon entropy calculation
- `TimeSeriesAnomalyAtom<T>` - Z-score anomaly detection
- `BurstDetectionAtom<T>` - Spike detection
- `WaveOrchestratorAtom<TInput, TOutput>` - Multi-wave processing

**When to use:** You need to analyze data and emit findings

```csharp
public class AnomalyDetectorAtom
{
    private readonly TimeSeriesAnomalyAtom<double> _detector;

    public AnomalyDetectorAtom(SignalSink signals)
    {
        _detector = new TimeSeriesAnomalyAtom<double>(
            windowSize: 100,
            zScoreThreshold: 3.0,
            signals: signals);
    }

    public async Task RecordMetric(string metricName, double value)
    {
        var result = await _detector.AnalyzeAsync(metricName, value);

        if (result.IsAnomaly)
        {
            // Atom already emitted "anomaly.detected" signal
            // You can add custom logic here
        }
    }
}
```

### 4. Orchestrator Atoms
**Purpose:** Coordinate the execution of other atoms

**Examples:**
- `SignalWaveExecutor` - Staged reactive pipelines
- `DependencyCoordinator<T>` - Dependency graph execution
- `StagedPipelineBuilder<T>` - Fluent pipeline construction

**When to use:** You need multi-stage coordination

```csharp
public class OrderProcessingOrchestrator
{
    private readonly DependencyCoordinator<OrderStep> _coordinator;

    public OrderProcessingOrchestrator(SignalSink signals)
    {
        _coordinator = new DependencyCoordinator<OrderStep>(
            ExecuteStepAsync,
            maxConcurrency: 10,
            sink: signals);

        // Define pipeline
        _coordinator.AddOperation("validate", validateStep);
        _coordinator.AddOperation("inventory", inventoryStep, dependsOn: "validate");
        _coordinator.AddOperation("payment", paymentStep, dependsOn: "validate");
        _coordinator.AddOperation("ship", shipStep,
            dependsOn: new[] { "inventory", "payment" });
    }

    public async Task ProcessOrder(Order order)
    {
        await _coordinator.ExecuteAsync();
        // Automatically runs: validate → (inventory ∥ payment) → ship
    }
}
```

### 5. Utility Atoms
**Purpose:** Provide cross-cutting infrastructure

**Examples:**
- `RateLimitAtom` - Token bucket rate limiting
- `WindowSizeAtom` - Dynamic SignalSink capacity management
- `ResponsibilitySignalManager` - Delivery acknowledgement tracking

**When to use:** You need reusable infrastructure components

```csharp
public class ApiGatewayAtom
{
    private readonly RateLimitAtom _rateLimiter;

    public ApiGatewayAtom(SignalSink signals)
    {
        _rateLimiter = new RateLimitAtom(
            signals,
            new RateLimitOptions
            {
                InitialRatePerSecond = 100,
                Burst = 200
            });
    }

    public async Task<bool> TryProcessRequest(Request request)
    {
        using var lease = await _rateLimiter.AcquireAsync();

        if (!lease.IsAcquired)
        {
            // Rate limited - atom already emitted "rate.limited" signal
            return false;
        }

        // Process request
        return true;
    }
}
```

---

## Building Your First Atom

### Step 1: Define the Atom's Responsibility

Ask yourself:
- What **one thing** does this atom do?
- What **signals** will it emit?
- What **signals** will it react to?

Example: "This atom validates email addresses and emits validation results"

### Step 2: Create the Atom Class

```csharp
using Mostlylucid.Ephemeral;

namespace MyApp.Atoms;

public class EmailValidatorAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly EphemeralWorkCoordinator<EmailValidationRequest> _coordinator;

    public EmailValidatorAtom(SignalSink signals, EmailValidatorOptions options)
    {
        _signals = signals;

        _coordinator = new EphemeralWorkCoordinator<EmailValidationRequest>(
            ValidateEmailAsync,
            new EphemeralOptions
            {
                MaxConcurrency = options.MaxConcurrency,
                MaxTrackedOperations = options.MaxTracked,
                Signals = signals
            });
    }

    public async Task ValidateAsync(string email, string requestId)
    {
        await _coordinator.EnqueueAsync(new EmailValidationRequest(email, requestId));
    }

    private async Task ValidateEmailAsync(EmailValidationRequest request, CancellationToken ct)
    {
        // Emit processing signal
        _signals.Raise("email.validating", request.RequestId);

        try
        {
            var isValid = await PerformValidation(request.Email, ct);

            if (isValid)
                _signals.Raise("email.valid", request.RequestId);
            else
                _signals.Raise("email.invalid", request.RequestId);
        }
        catch (Exception ex)
        {
            _signals.Raise("email.validation_failed", request.RequestId);
            throw;
        }
    }

    private async Task<bool> PerformValidation(string email, CancellationToken ct)
    {
        // Your validation logic here
        await Task.Delay(50, ct); // Simulate async work
        return email.Contains("@");
    }

    public async ValueTask DisposeAsync()
    {
        await _coordinator.DisposeAsync();
    }
}

public record EmailValidationRequest(string Email, string RequestId);

public class EmailValidatorOptions
{
    public int MaxConcurrency { get; init; } = 10;
    public int MaxTracked { get; init; } = 1000;
}
```

### Step 3: Register in Dependency Injection

```csharp
// In your Startup.cs or Program.cs
services.AddSingleton<SignalSink>(sp => new SignalSink(maxCapacity: 10000));
services.AddSingleton<EmailValidatorAtom>();
services.AddSingleton<OrderProcessorAtom>();
services.AddSingleton<NotificationAtom>();
```

### Step 4: Use the Atom

```csharp
public class UserRegistrationService
{
    private readonly EmailValidatorAtom _emailValidator;
    private readonly SignalSink _signals;

    public UserRegistrationService(EmailValidatorAtom emailValidator, SignalSink signals)
    {
        _emailValidator = emailValidator;
        _signals = signals;

        // Subscribe to validation results
        _signals.Subscribe(signal =>
        {
            if (signal.Signal == "email.valid")
            {
                Console.WriteLine($"Email {signal.Key} is valid!");
            }
            else if (signal.Signal == "email.invalid")
            {
                Console.WriteLine($"Email {signal.Key} is invalid!");
            }
        });
    }

    public async Task RegisterUser(string email)
    {
        var requestId = Guid.NewGuid().ToString();
        await _emailValidator.ValidateAsync(email, requestId);
    }
}
```

---

## Atom Communication Patterns

### Pattern 1: Request-Response (Signal-Based)

```csharp
public class RequestorAtom
{
    private readonly SignalSink _signals;

    public async Task<string> RequestData(string id)
    {
        var responseReceived = new TaskCompletionSource<string>();

        using var subscription = _signals.Subscribe(signal =>
        {
            if (signal.Signal == $"response.data:{id}")
            {
                responseReceived.SetResult(signal.Key);
            }
        });

        // Send request signal
        _signals.Raise("request.data", id);

        // Wait for response signal
        return await responseReceived.Task;
    }
}

public class ResponderAtom
{
    public ResponderAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "request.data")
            {
                var id = signal.Key;
                var data = FetchData(id);
                signals.Raise($"response.data:{id}", data);
            }
        });
    }
}
```

### Pattern 2: Publish-Subscribe (Broadcast)

```csharp
public class EventPublisherAtom
{
    private readonly SignalSink _signals;

    public void PublishEvent(string eventType, string payload)
    {
        // Broadcast to all interested subscribers
        _signals.Raise($"event.{eventType}", payload);
    }
}

public class SubscriberAtom1
{
    public SubscriberAtom1(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal.StartsWith("event.order"))
            {
                HandleOrderEvent(signal);
            }
        });
    }
}

public class SubscriberAtom2
{
    public SubscriberAtom2(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal.StartsWith("event."))
            {
                LogEvent(signal);
            }
        });
    }
}
```

### Pattern 3: Pipeline (Sequential Processing)

```csharp
public class Stage1Atom
{
    public Stage1Atom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "input.raw")
            {
                var processed = ProcessStage1(signal.Key);
                signals.Raise("stage1.complete", processed);
            }
        });
    }
}

public class Stage2Atom
{
    public Stage2Atom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "stage1.complete")
            {
                var processed = ProcessStage2(signal.Key);
                signals.Raise("stage2.complete", processed);
            }
        });
    }
}

public class Stage3Atom
{
    public Stage3Atom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "stage2.complete")
            {
                var final = ProcessStage3(signal.Key);
                signals.Raise("pipeline.complete", final);
            }
        });
    }
}
```

### Pattern 4: Aggregation (Fan-In)

```csharp
public class AggregatorAtom
{
    private readonly Dictionary<string, List<string>> _results = new();
    private readonly SignalSink _signals;

    public AggregatorAtom(SignalSink signals)
    {
        _signals = signals;

        signals.Subscribe(signal =>
        {
            // Collect results from multiple workers
            if (signal.Signal.StartsWith("worker.result"))
            {
                var batchId = signal.Key;

                if (!_results.ContainsKey(batchId))
                    _results[batchId] = new List<string>();

                _results[batchId].Add(signal.Signal);

                // If we have all results, aggregate
                if (_results[batchId].Count >= 5)
                {
                    var aggregated = Aggregate(_results[batchId]);
                    _signals.Raise("batch.complete", batchId);
                    _results.Remove(batchId);
                }
            }
        });
    }
}
```

### Pattern 5: Scatter-Gather (Fan-Out + Fan-In)

```csharp
public class CoordinatorAtom
{
    private readonly SignalSink _signals;

    public async Task ProcessBatch(string[] items)
    {
        var batchId = Guid.NewGuid().ToString();

        // Scatter: Send work to multiple workers
        foreach (var item in items)
        {
            _signals.Raise("work.dispatch", $"{batchId}:{item}");
        }

        // Gather: Wait for all results
        var resultsReceived = 0;
        var tcs = new TaskCompletionSource();

        using var subscription = _signals.Subscribe(signal =>
        {
            if (signal.Signal.StartsWith($"work.complete:{batchId}"))
            {
                resultsReceived++;
                if (resultsReceived >= items.Length)
                {
                    tcs.SetResult();
                }
            }
        });

        await tcs.Task;
        _signals.Raise("batch.complete", batchId);
    }
}

public class WorkerAtom
{
    public WorkerAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "work.dispatch")
            {
                var parts = signal.Key.Split(':');
                var batchId = parts[0];
                var item = parts[1];

                var result = ProcessItem(item);
                signals.Raise($"work.complete:{batchId}", result);
            }
        });
    }
}
```

---

## Advanced Atom Patterns

### Pattern 1: Self-Regulating Atom (Adaptive Behavior)

```csharp
public class AdaptiveProcessorAtom
{
    private readonly EphemeralWorkCoordinator<Item> _coordinator;
    private readonly SignalSink _signals;
    private int _currentConcurrency = 4;

    public AdaptiveProcessorAtom(SignalSink signals)
    {
        _signals = signals;

        _coordinator = new EphemeralWorkCoordinator<Item>(
            ProcessAsync,
            new EphemeralOptions
            {
                EnableDynamicConcurrency = true,  // Allow runtime changes
                MaxConcurrency = _currentConcurrency,
                Signals = signals
            });

        // Adapt to system load signals
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "system.load.high")
            {
                _currentConcurrency = Math.Max(1, _currentConcurrency / 2);
                _coordinator.SetMaxConcurrency(_currentConcurrency);
                _signals.Raise("atom.concurrency_reduced", _currentConcurrency.ToString());
            }
            else if (signal.Signal == "system.load.normal")
            {
                _currentConcurrency = Math.Min(16, _currentConcurrency * 2);
                _coordinator.SetMaxConcurrency(_currentConcurrency);
                _signals.Raise("atom.concurrency_increased", _currentConcurrency.ToString());
            }
        });
    }
}
```

### Pattern 2: Hierarchical Atoms (Parent-Child)

```csharp
public class ParentAtom
{
    private readonly List<ChildAtom> _children = new();
    private readonly SignalSink _signals;

    public ParentAtom(SignalSink signals, int childCount)
    {
        _signals = signals;

        // Create child atoms
        for (int i = 0; i < childCount; i++)
        {
            _children.Add(new ChildAtom(signals, $"child-{i}"));
        }

        // Monitor child completion signals
        signals.Subscribe(signal =>
        {
            if (signal.Signal.StartsWith("child.completed"))
            {
                var completedCount = signals.Sense()
                    .Count(s => s.Signal.StartsWith("child.completed"));

                if (completedCount >= childCount)
                {
                    _signals.Raise("parent.all_children_complete");
                }
            }
        });
    }

    public async Task DistributeWork(Item[] items)
    {
        // Round-robin distribution to children
        for (int i = 0; i < items.Length; i++)
        {
            var child = _children[i % _children.Count];
            await child.ProcessAsync(items[i]);
        }
    }
}

public class ChildAtom
{
    private readonly string _name;
    private readonly SignalSink _signals;

    public ChildAtom(SignalSink signals, string name)
    {
        _name = name;
        _signals = signals;
    }

    public async Task ProcessAsync(Item item)
    {
        // Do work
        await Task.Delay(100);

        // Signal completion to parent
        _signals.Raise($"child.completed:{_name}", item.Id);
    }
}
```

### Pattern 3: Circuit Breaker Atom

```csharp
public class CircuitBreakerAtom
{
    private readonly SignalSink _signals;
    private readonly EphemeralWorkCoordinator<Request> _coordinator;
    private int _failureCount = 0;
    private DateTime _circuitOpenedAt = DateTime.MinValue;
    private const int FailureThreshold = 5;
    private readonly TimeSpan _cooldownPeriod = TimeSpan.FromSeconds(30);

    public CircuitBreakerAtom(SignalSink signals)
    {
        _signals = signals;

        _coordinator = new EphemeralWorkCoordinator<Request>(
            ProcessRequestAsync,
            new EphemeralOptions
            {
                CancelOnSignals = new HashSet<string> { "circuit.open" },
                Signals = signals
            });

        // Monitor for failures
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "request.failed")
            {
                _failureCount++;

                if (_failureCount >= FailureThreshold)
                {
                    _circuitOpenedAt = DateTime.UtcNow;
                    _signals.Raise("circuit.open");
                }
            }
            else if (signal.Signal == "request.success")
            {
                _failureCount = Math.Max(0, _failureCount - 1);
            }
        });

        // Automatic recovery
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000);

                if (_circuitOpenedAt != DateTime.MinValue)
                {
                    var elapsed = DateTime.UtcNow - _circuitOpenedAt;
                    if (elapsed > _cooldownPeriod)
                    {
                        _circuitOpenedAt = DateTime.MinValue;
                        _failureCount = 0;
                        _signals.Raise("circuit.closed");
                    }
                }
            }
        });
    }
}
```

---

## Testing Atoms

### Unit Testing with Signal Verification

```csharp
using Xunit;
using Mostlylucid.Ephemeral;

public class EmailValidatorAtomTests
{
    [Fact]
    public async Task ValidEmail_EmitsValidSignal()
    {
        // Arrange
        var signals = new SignalSink();
        var atom = new EmailValidatorAtom(signals, new EmailValidatorOptions());
        var signalReceived = false;

        signals.Subscribe(signal =>
        {
            if (signal.Signal == "email.valid")
                signalReceived = true;
        });

        // Act
        await atom.ValidateAsync("test@example.com", "req-1");
        await Task.Delay(200); // Wait for async processing

        // Assert
        Assert.True(signalReceived);
    }

    [Fact]
    public async Task InvalidEmail_EmitsInvalidSignal()
    {
        // Arrange
        var signals = new SignalSink();
        var atom = new EmailValidatorAtom(signals, new EmailValidatorOptions());
        var invalidSignalReceived = false;

        signals.Subscribe(signal =>
        {
            if (signal.Signal == "email.invalid")
                invalidSignalReceived = true;
        });

        // Act
        await atom.ValidateAsync("not-an-email", "req-2");
        await Task.Delay(200);

        // Assert
        Assert.True(invalidSignalReceived);
    }

    [Fact]
    public async Task MultipleAtoms_CoordinateViaSignals()
    {
        // Arrange
        var signals = new SignalSink();
        var validator = new EmailValidatorAtom(signals, new EmailValidatorOptions());
        var processor = new OrderProcessorAtom(signals);
        var orderProcessed = false;

        signals.Subscribe(signal =>
        {
            if (signal.Signal == "order.processed")
                orderProcessed = true;
        });

        // Act
        await validator.ValidateAsync("test@example.com", "order-1");
        await Task.Delay(500); // Wait for signal chain

        // Assert
        Assert.True(orderProcessed);
    }
}
```

### Integration Testing with Real Signal Flow

```csharp
public class AtomPipelineIntegrationTests
{
    [Fact]
    public async Task CompletePipeline_ProcessesSuccessfully()
    {
        // Arrange
        var signals = new SignalSink();

        var stage1 = new Stage1ProcessorAtom(signals);
        var stage2 = new Stage2ProcessorAtom(signals);
        var stage3 = new Stage3ProcessorAtom(signals);

        var pipelineComplete = new TaskCompletionSource();

        signals.Subscribe(signal =>
        {
            if (signal.Signal == "pipeline.complete")
                pipelineComplete.SetResult();
        });

        // Act
        signals.Raise("input.raw", "test-data");

        // Assert
        await pipelineComplete.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Verify signal chain
        var allSignals = signals.Sense();
        Assert.Contains(allSignals, s => s.Signal == "stage1.complete");
        Assert.Contains(allSignals, s => s.Signal == "stage2.complete");
        Assert.Contains(allSignals, s => s.Signal == "pipeline.complete");
    }
}
```

---

## Best Practices

### ✅ DO

1. **Keep atoms focused** - One clear responsibility per atom
2. **Emit signals liberally** - Signal important state changes
3. **Use pattern matching** - `signal.StartsWith("order.*")` for flexibility
4. **Set resource bounds** - Always configure `MaxTrackedOperations`, `MaxConcurrency`
5. **Implement IAsyncDisposable** - Clean up resources properly
6. **Document signal contracts** - What signals does your atom emit/consume?
7. **Use typed signal keys** - `SignalKey<T>` for compile-time safety
8. **Test signal flows** - Verify atoms coordinate correctly via signals

### ❌ DON'T

1. **Don't create god atoms** - Resist the urge to put everything in one atom
2. **Don't directly call other atoms** - Use signals, not direct method calls
3. **Don't ignore resource limits** - Unbounded atoms cause memory leaks
4. **Don't emit signal spam** - Use sampling for high-frequency signals
5. **Don't block in signal handlers** - Keep subscribers fast and async
6. **Don't create circular dependencies** - Use `SignalConstraints.BlockCycles`
7. **Don't ignore disposal** - Always dispose coordinators and subscriptions
8. **Don't over-couple to signal names** - Use pattern matching for flexibility

---

## Signal Naming Conventions

### Recommended Patterns

```csharp
// ✅ Good: Hierarchical, self-documenting
"order.created"
"order.validated"
"order.payment.succeeded"
"order.payment.failed"
"order.shipped"
"order.completed"

// ✅ Good: Action-oriented
"user.login.attempted"
"user.login.succeeded"
"user.login.failed"

// ✅ Good: State transitions
"circuit.open"
"circuit.half_open"
"circuit.closed"

// ❌ Bad: Vague
"thing"
"done"
"event"

// ❌ Bad: Not hierarchical
"orderCreated"
"orderValidated"
// (Can't pattern match "order.*")
```

### Signal Hierarchy

```
entity.action.result
  │      │      │
  │      │      └─ succeeded, failed, cancelled
  │      └──────── created, updated, deleted, validated
  └─────────────── user, order, payment, inventory
```

---

## Performance Considerations

### Memory Management

```csharp
// Configure bounded windows
var options = new EphemeralOptions
{
    MaxTrackedOperations = 1000,        // Max operations in window
    MaxOperationLifetime = TimeSpan.FromMinutes(5),  // Auto-evict old ops
    EnableOperationEcho = true,         // Keep signal history briefly after eviction
    OperationEchoRetention = TimeSpan.FromMinutes(1),  // Echo TTL
    OperationEchoCapacity = 256         // Max echoes
};
```

### Signal Window Sizing

```csharp
// For high-throughput systems
var signals = new SignalSink(maxCapacity: 100000);

// For low-memory environments
var signals = new SignalSink(maxCapacity: 1000);

// With auto-cleanup
var signals = new SignalSink(
    maxCapacity: 10000,
    maxAge: TimeSpan.FromMinutes(10)  // Auto-remove old signals
);
```

### Concurrency Tuning

```csharp
// CPU-bound work: Use processor count
MaxConcurrency = Environment.ProcessorCount

// I/O-bound work: Higher concurrency
MaxConcurrency = Environment.ProcessorCount * 4

// Memory-intensive work: Lower concurrency
MaxConcurrency = Math.Max(1, Environment.ProcessorCount / 2)
```

---

## Real-World Example: E-Commerce Order Processing

### Full Implementation

```csharp
// Shared signal sink
var signals = new SignalSink(maxCapacity: 50000);

// Atom 1: Order Validator
public class OrderValidatorAtom
{
    public OrderValidatorAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "order.submitted")
            {
                var orderId = signal.Key;
                var isValid = ValidateOrder(orderId);

                if (isValid)
                    signals.Raise("order.valid", orderId);
                else
                    signals.Raise("order.invalid", orderId);
            }
        });
    }
}

// Atom 2: Inventory Manager
public class InventoryAtom
{
    public InventoryAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "order.valid")
            {
                var orderId = signal.Key;
                var available = CheckInventory(orderId);

                if (available)
                {
                    ReserveInventory(orderId);
                    signals.Raise("inventory.reserved", orderId);
                }
                else
                {
                    signals.Raise("inventory.unavailable", orderId);
                }
            }
        });
    }
}

// Atom 3: Payment Processor
public class PaymentAtom
{
    public PaymentAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "inventory.reserved")
            {
                var orderId = signal.Key;
                var charged = ChargePayment(orderId);

                if (charged)
                    signals.Raise("payment.succeeded", orderId);
                else
                    signals.Raise("payment.failed", orderId);
            }
        });
    }
}

// Atom 4: Fulfillment
public class FulfillmentAtom
{
    public FulfillmentAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            if (signal.Signal == "payment.succeeded")
            {
                var orderId = signal.Key;
                CreateShipment(orderId);
                signals.Raise("order.shipped", orderId);
            }
        });
    }
}

// Atom 5: Notification
public class NotificationAtom
{
    public NotificationAtom(SignalSink signals)
    {
        signals.Subscribe(signal =>
        {
            var orderId = signal.Key;

            switch (signal.Signal)
            {
                case "order.shipped":
                    SendEmail(orderId, "Your order has shipped!");
                    break;
                case "payment.failed":
                    SendEmail(orderId, "Payment failed. Please update payment method.");
                    break;
                case "inventory.unavailable":
                    SendEmail(orderId, "Item out of stock. We'll notify you when available.");
                    break;
            }
        });
    }
}

// Composition
var validator = new OrderValidatorAtom(signals);
var inventory = new InventoryAtom(signals);
var payment = new PaymentAtom(signals);
var fulfillment = new FulfillmentAtom(signals);
var notifications = new NotificationAtom(signals);

// Trigger the pipeline
signals.Raise("order.submitted", "order-12345");

// Flow:
// order.submitted → validator → order.valid → inventory → inventory.reserved
// → payment → payment.succeeded → fulfillment → order.shipped → notification
```

---

## Conclusion

**Atoms are the fundamental building blocks of signal-driven architecture.**

Key takeaways:
1. **Single Responsibility** - Each atom does one thing well
2. **Signal-Driven** - Atoms communicate via signals, not direct calls
3. **Composable** - Complex systems emerge from simple atom interactions
4. **Bounded** - Always configure resource limits
5. **Observable** - Emit signals for monitoring and debugging

By composing small, focused atoms, you build maintainable, scalable, and observable systems that are "hitherto impractical in .NET."

---

## Further Reading

- [SIGNALS_PATTERN.md](SIGNALS_PATTERN.md) - Deep dive into signal patterns
- [README.md](README.md) - Library overview and quick start
- [Examples/](Examples/) - Production-ready atom examples
- [Atoms/](src/mostlylucid.ephemeral.atoms.*/) - Built-in atom implementations
