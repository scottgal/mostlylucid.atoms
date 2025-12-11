namespace Mostlylucid.Ephemeral;

/// <summary>
///     Configuration options for ephemeral work coordinators.
/// </summary>
public sealed class EphemeralOptions
{
    /// <summary>
    ///     Max number of operations to run concurrently overall.
    ///     Default: number of CPU cores.
    /// </summary>
    public int MaxConcurrency { get; init; } = Environment.ProcessorCount;

    /// <summary>
    ///     Allow adjusting concurrency at runtime. Defaults to false for fastest hot-path.
    /// </summary>
    public bool EnableDynamicConcurrency { get; init; } = false;

    /// <summary>
    ///     Max number of operations to retain in the in-memory window.
    ///     Oldest entries are dropped first (LRU-style).
    ///
    ///     IMPORTANT: This also controls the internal channel capacity for pending work.
    ///     When using DeferOnSignals or bulk enqueuing with EnqueueManyAsync, ensure this
    ///     value is >= the number of items you plan to enqueue while deferred. If the channel
    ///     fills up while the consumer is deferred, EnqueueAsync/EnqueueManyAsync will block.
    ///
    ///     Example: If preloading 1000 jobs with DeferOnSignals, set MaxTrackedOperations to
    ///     at least 1000 to prevent blocking during bulk enqueue.
    /// </summary>
    public int MaxTrackedOperations { get; init; } = 200;

    /// <summary>
    ///     Enable a short-lived “echo” of the signals emitted by an operation when it's trimmed.
    ///     Defaults to true so the last-waves of signal activity remain queryable for a moment after eviction.
    /// </summary>
    public bool EnableOperationEcho { get; init; } = true;

    /// <summary>
    ///     How long echoed signal copies stay available.
    /// </summary>
    public TimeSpan OperationEchoRetention { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Maximum number of echoed entries retained.
    /// </summary>
    public int OperationEchoCapacity { get; init; } = 256;

    /// <summary>
    ///     Optional max age for tracked operations.
    ///     Older entries are dropped during cleanup sweeps.
    /// </summary>
    public TimeSpan? MaxOperationLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Optional callback to observe a snapshot of the current window.
    ///     Runs on the caller's thread after each operation completes — keep it cheap.
    /// </summary>
    public Action<IReadOnlyCollection<EphemeralOperationSnapshot>>? OnSample { get; init; }

    /// <summary>
    ///     Max concurrency per key for keyed pipelines.
    ///     Default 1 = strictly sequential execution per key.
    ///     Only used by the keyed overload.
    /// </summary>
    public int MaxConcurrencyPerKey { get; init; } = 1;

    /// <summary>
    ///     Enable fair scheduling across keys.
    ///     When enabled, keys with more pending work are deprioritized
    ///     to prevent hot keys from starving cold keys.
    ///     Default: false (FIFO ordering).
    /// </summary>
    public bool EnableFairScheduling { get; init; } = false;

    /// <summary>
    ///     Maximum pending items per key before new items for that key are deprioritized.
    ///     Only used when EnableFairScheduling is true.
    ///     Default: 10.
    /// </summary>
    public int FairSchedulingThreshold { get; init; } = 10;

    /// <summary>
    ///     Optional global signal sink.
    ///     When set, signals raised on operations are also sent here.
    /// </summary>
    public SignalSink? Signals { get; init; }

    /// <summary>
    ///     Optional callback when a signal is raised.
    ///     Runs synchronously on the operation's thread - keep it fast.
    /// </summary>
    public Action<SignalEvent>? OnSignal { get; init; }

    /// <summary>
    ///     Optional async callback when a signal is raised.
    ///     Signals are dispatched to a background queue for non-blocking processing.
    ///     The coordinator does not wait for these handlers to complete.
    ///     Use for I/O-bound signal handling (logging to external services, etc.).
    /// </summary>
    public Func<SignalEvent, CancellationToken, Task>? OnSignalAsync { get; init; }

    /// <summary>
    ///     Optional callback when a signal is retracted (removed).
    ///     Runs synchronously on the operation's thread - keep it fast.
    /// </summary>
    public Action<SignalRetractedEvent>? OnSignalRetracted { get; init; }

    /// <summary>
    ///     Optional async callback when a signal is retracted (removed).
    ///     Dispatched to a background queue for non-blocking processing.
    /// </summary>
    public Func<SignalRetractedEvent, CancellationToken, Task>? OnSignalRetractedAsync { get; init; }

    /// <summary>
    ///     Maximum concurrent async signal handlers running at once.
    ///     Only applies when OnSignalAsync or OnSignalRetractedAsync is set.
    ///     Default: 4.
    /// </summary>
    public int MaxConcurrentSignalHandlers { get; init; } = 4;

    /// <summary>
    ///     Maximum queued signals waiting for async processing.
    ///     If exceeded, oldest signals are dropped.
    ///     Only applies when OnSignalAsync or OnSignalRetractedAsync is set.
    ///     Default: 1000.
    /// </summary>
    public int MaxQueuedSignals { get; init; } = 1000;

    /// <summary>
    ///     Signals this coordinator may raise. Self-documenting, no runtime enforcement.
    /// </summary>
    public IReadOnlyList<string>? Emits { get; init; }

    /// <summary>
    ///     Signals this coordinator listens for. Self-documenting, no runtime enforcement.
    /// </summary>
    public IReadOnlyList<string>? Listens { get; init; }

    /// <summary>
    ///     Constraints for signal propagation (cycle detection, depth limits, terminal signals).
    ///     Default: null (no constraints enforced).
    /// </summary>
    public SignalConstraints? SignalConstraints { get; init; }

    /// <summary>
    ///     Signals that cause pending items to be skipped (not executed).
    ///     When any of these signals are present in the recent window, new items won't start.
    ///     Use for circuit-breaker patterns: ["circuit-open", "rate-limited"].
    /// </summary>
    public IReadOnlySet<string>? CancelOnSignals { get; init; }

    /// <summary>
    ///     Signals that cause pending items to wait before starting.
    ///     When present, items are delayed until the signal ages out of the window OR a ResumeOnSignals signal is raised.
    ///     Use for backpressure: ["backpressure", "slow-downstream"].
    /// </summary>
    public IReadOnlySet<string>? DeferOnSignals { get; init; }

    /// <summary>
    ///     Signals that cancel/override active defer signals and immediately resume processing.
    ///     When any of these signals are raised, DeferOnSignals are ignored and processing resumes.
    ///     Use for explicit triggers: ["batch.ready", "system.resume", "load.complete"].
    ///     Pattern matching supported (e.g., "batch.*" matches "batch.ready", "batch.complete").
    ///
    ///     PATTERN: Preload and Trigger
    ///     1. Raise defer signal: signals.Raise("batch.loading")
    ///     2. Set DeferOnSignals = ["batch.loading"], ResumeOnSignals = ["batch.ready"]
    ///     3. Bulk enqueue: await coordinator.EnqueueManyAsync(jobs) // Jobs won't process yet
    ///     4. Trigger: signals.Raise("batch.ready") // Processing starts immediately
    ///
    ///     NOTE: Ensure MaxTrackedOperations >= number of jobs to prevent blocking during enqueue.
    /// </summary>
    public IReadOnlySet<string>? ResumeOnSignals { get; init; }

    /// <summary>
    ///     How long to wait when DeferOnSignals are present before rechecking.
    ///     Default: 100ms.
    /// </summary>
    public TimeSpan DeferCheckInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Maximum times to defer before giving up and running anyway.
    ///     Default: 50 (5 seconds at 100ms intervals).
    /// </summary>
    public int MaxDeferAttempts { get; init; } = 50;

    /// <summary>
    ///     Signals that trigger immediate clearing of the signal window.
    ///     When any of these signals are raised, the sink's window is cleared.
    ///     Use for explicit state reset: ["reset", "clear", "restart"].
    ///     Pattern matching supported (e.g., "clear.*" matches "clear.all", "clear.errors").
    /// </summary>
    public IReadOnlySet<string>? ClearOnSignals { get; init; }

    /// <summary>
    ///     Whether to clear the entire sink or only signals matching the clear signal pattern.
    ///     Default: false (clear entire sink).
    ///     When true: "clear.errors" will only clear signals matching "error.*".
    /// </summary>
    public bool ClearOnSignalsUsePattern { get; init; } = false;

    /// <summary>
    ///     Signals that trigger this coordinator to complete intake and begin draining.
    ///     Default: ["coordinator.drain.all", "coordinator.drain.id"] (listens for global and targeted drain requests).
    ///     Pattern matching supported - coordinator will drain if any signal in the window matches these patterns.
    ///     Set to empty/null to disable signal-based draining.
    /// </summary>
    public IReadOnlySet<string>? DrainOnSignals { get; init; } = new HashSet<string>
    {
        "coordinator.drain.all",
        "coordinator.drain.id"
    };

    /// <summary>
    ///     Unique identifier for this coordinator used in drain signal matching.
    ///     When set, coordinator responds to "coordinator.drain.id" signals with Key matching this ID.
    ///     If null, coordinator only responds to "coordinator.drain.all".
    /// </summary>
    public string? CoordinatorId { get; init; }
}