namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
///     Marks a method as a signal-driven job.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EphemeralJobAttribute : Attribute
{
    public EphemeralJobAttribute(string triggerSignal)
    {
        TriggerSignal = triggerSignal ?? throw new ArgumentNullException(nameof(triggerSignal));
    }

    /// <summary>
    ///     Signal pattern (supports '*' and '?' wildcards) that triggers this job.
    /// </summary>
    public string TriggerSignal { get; }

    /// <summary>
    ///     Optional static key to tag the operation emitted when the job runs.
    ///     Use <see cref="KeyFromSignal" /> or <see cref="KeyFromPayload" /> for dynamic keys.
    /// </summary>
    public string? OperationKey { get; set; }

    /// <summary>
    ///     Priority of this job (lower = higher priority). Default is 0 (normal).
    ///     Jobs with lower priority values are executed before jobs with higher values.
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    ///     Maximum concurrent executions of this job. Default is 1 (sequential).
    ///     Set to -1 for unlimited concurrency.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    ///     When true, extracts the key from SignalEvent.Key property.
    ///     Takes precedence over <see cref="OperationKey" />.
    /// </summary>
    public bool KeyFromSignal { get; set; }

    /// <summary>
    ///     Property or field name on the signal payload to extract as the operation key.
    ///     Requires the method to receive a typed payload. Dot notation supported (e.g., "User.Id").
    ///     Takes precedence over <see cref="KeyFromSignal" /> and <see cref="OperationKey" />.
    /// </summary>
    public string? KeyFromPayload { get; set; }

    /// <summary>
    ///     Timeout for the job execution. Default is null (no timeout).
    ///     Specify in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 0;

    /// <summary>
    ///     Signals emitted when this job starts.
    /// </summary>
    public string[]? EmitOnStart { get; set; }

    /// <summary>
    ///     Signals emitted when this job completes successfully.
    /// </summary>
    public string[]? EmitOnComplete { get; set; }

    /// <summary>
    ///     Signals emitted when this job fails.
    /// </summary>
    public string[]? EmitOnFailure { get; set; }

    /// <summary>
    ///     If true, exceptions are swallowed and logged as signals instead of propagating.
    ///     Default is false.
    /// </summary>
    public bool SwallowExceptions { get; set; }

    /// <summary>
    ///     Maximum retry attempts on failure. Default is 0 (no retries).
    /// </summary>
    public int MaxRetries { get; set; } = 0;

    /// <summary>
    ///     Base delay between retries in milliseconds. Uses exponential backoff.
    ///     Default is 100ms.
    /// </summary>
    public int RetryDelayMs { get; set; } = 100;

    /// <summary>
    ///     If true, pins the operation so it's never automatically evicted from the coordinator.
    ///     Use for long-running signal watchers that need to remain visible.
    ///     Default is false.
    /// </summary>
    public bool Pin { get; set; }

    /// <summary>
    ///     Duration in milliseconds after which the completed operation should be evicted.
    ///     Only applies if Pin is false. Default is 0 (uses coordinator default).
    /// </summary>
    public int ExpireAfterMs { get; set; } = 0;

    /// <summary>
    ///     Signals to await before starting the job.
    ///     The job will wait until all specified signals have been raised.
    ///     Supports wildcards.
    /// </summary>
    public string[]? AwaitSignals { get; set; }

    /// <summary>
    ///     Timeout in milliseconds for awaiting signals. Default is 0 (infinite wait).
    /// </summary>
    public int AwaitTimeoutMs { get; set; } = 0;

    /// <summary>
    ///     Processing lane for this job. Jobs in the same lane share concurrency control.
    ///     Format: "name" or "name:concurrency" (e.g., "fast", "io:4", "cpu-bound:8").
    ///     Default is null (uses the class default or shared default lane).
    /// </summary>
    public string? Lane { get; set; }
}

/// <summary>
///     Marks a parameter as the source for extracting the operation key.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class KeySourceAttribute : Attribute
{
    /// <summary>
    ///     Optional property path to extract from the parameter (e.g., "Id" or "User.Name").
    ///     If null, uses ToString() on the parameter value.
    /// </summary>
    public string? PropertyPath { get; set; }
}

/// <summary>
///     Marks a class as a container for signal-driven jobs with shared configuration.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EphemeralJobsAttribute : Attribute
{
    /// <summary>
    ///     Default priority for all jobs in this class (can be overridden per-method).
    /// </summary>
    public int DefaultPriority { get; set; } = 0;

    /// <summary>
    ///     Default max concurrency for all jobs in this class (can be overridden per-method).
    /// </summary>
    public int DefaultMaxConcurrency { get; set; } = 1;

    /// <summary>
    ///     Signal prefix prepended to all job signals in this class.
    /// </summary>
    public string? SignalPrefix { get; set; }

    /// <summary>
    ///     Default lane for all jobs in this class (can be overridden per-method).
    /// </summary>
    public string? DefaultLane { get; set; }
}

/// <summary>
///     Configures a processing lane with specific concurrency and behavior settings.
///     Apply to a class to define lane parameters that jobs can reference.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class EphemeralLaneAttribute : Attribute
{
    public EphemeralLaneAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    ///     Name of the lane (e.g., "fast", "slow", "io-bound", "cpu-bound").
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Maximum concurrent executions in this lane.
    ///     Default is 1 (sequential processing within the lane).
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    ///     Priority for scheduling this lane relative to others.
    ///     Lower = higher priority. Default is 0.
    /// </summary>
    public int Priority { get; set; } = 0;
}