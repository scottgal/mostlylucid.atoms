using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
/// Job descriptor that resolves from DI scope per execution.
/// </summary>
public sealed class ScopedJobDescriptor
{
    public Type JobType { get; init; } = null!;
    public MethodInfo Method { get; init; } = null!;
    public EphemeralJobAttribute Attribute { get; init; } = null!;
    public EphemeralJobsAttribute? ClassAttribute { get; init; }

    public string EffectiveTriggerSignal { get; init; } = "";
    public int EffectivePriority { get; init; }
    public int EffectiveMaxConcurrency { get; init; } = 1;
    public string Lane { get; init; } = "default";
    public int LaneMaxConcurrency { get; init; }
    public TimeSpan? Timeout => Attribute.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(Attribute.TimeoutMs) : null;
    public IReadOnlyList<string>? AwaitSignals => Attribute.AwaitSignals;
    public TimeSpan? AwaitTimeout => Attribute.AwaitTimeoutMs > 0 ? TimeSpan.FromMilliseconds(Attribute.AwaitTimeoutMs) : null;

    public bool Matches(SignalEvent signal) =>
        StringPatternMatcher.Matches(signal.Signal, EffectiveTriggerSignal);

    public string? ExtractKey(SignalEvent signal)
    {
        if (Attribute.KeyFromSignal)
            return signal.Key;
        return Attribute.OperationKey;
    }
}

/// <summary>
/// Signal-driven job runner that creates a DI scope per job execution.
/// Each job gets fresh scoped services (DbContext, storage atoms, etc.).
/// </summary>
public sealed class EphemeralScopedJobRunner : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SignalSink _signals;
    private EphemeralKeyedWorkCoordinator<ScopedJobInvocation, string> _coordinator = null!;
    private readonly IReadOnlyList<ScopedJobDescriptor> _jobs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, SemaphoreSlim> _laneGates = new();
    private readonly Dictionary<string, SemaphoreSlim> _jobGates = new();

    /// <summary>
    /// Creates a scoped job runner from registered job types.
    /// </summary>
    /// <param name="serviceProvider">Root service provider for creating scopes</param>
    /// <param name="signals">Signal sink for job triggers</param>
    /// <param name="jobTypes">Types decorated with [EphemeralJobs] and [EphemeralJob]</param>
    /// <param name="options">Coordinator options</param>
    public EphemeralScopedJobRunner(
        IServiceProvider serviceProvider,
        SignalSink signals,
        IEnumerable<Type> jobTypes,
        EphemeralOptions? options = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));

        _jobs = ScanJobTypes(jobTypes);
        if (_jobs.Count == 0)
            throw new ArgumentException("No attributed jobs were discovered.", nameof(jobTypes));

        InitializeGates();
        InitializeCoordinator(options);

        _signals.SignalRaised += OnSignal;
    }

    /// <summary>
    /// Creates a scoped job runner by scanning assemblies for job types.
    /// </summary>
    public EphemeralScopedJobRunner(
        IServiceProvider serviceProvider,
        SignalSink signals,
        IEnumerable<Assembly> assemblies,
        EphemeralOptions? options = null)
        : this(serviceProvider, signals, ScanAssembliesForJobTypes(assemblies), options)
    {
    }

    public int PendingCount => _coordinator.PendingCount;
    public int ActiveCount => _coordinator.ActiveCount;
    public IReadOnlyList<ScopedJobDescriptor> Jobs => _jobs;
    public IReadOnlyCollection<string> Lanes => _laneGates.Keys;

    private static IEnumerable<Type> ScanAssembliesForJobTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                // Include types with EphemeralJobsAttribute OR any method with EphemeralJobAttribute
                if (type.GetCustomAttribute<EphemeralJobsAttribute>() != null)
                {
                    yield return type;
                    continue;
                }

                var hasJobMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(m => m.GetCustomAttribute<EphemeralJobAttribute>() != null);

                if (hasJobMethods)
                    yield return type;
            }
        }
    }

    private static IReadOnlyList<ScopedJobDescriptor> ScanJobTypes(IEnumerable<Type> jobTypes)
    {
        var descriptors = new List<ScopedJobDescriptor>();

        foreach (var type in jobTypes)
        {
            var classAttr = type.GetCustomAttribute<EphemeralJobsAttribute>();
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<EphemeralJobAttribute>();
                if (attr == null) continue;

                var prefix = classAttr?.SignalPrefix;
                var triggerSignal = !string.IsNullOrEmpty(prefix)
                    ? $"{prefix}.{attr.TriggerSignal}"
                    : attr.TriggerSignal;

                var (laneName, laneConcurrency) = ParseLane(attr.Lane ?? classAttr?.DefaultLane);

                descriptors.Add(new ScopedJobDescriptor
                {
                    JobType = type,
                    Method = method,
                    Attribute = attr,
                    ClassAttribute = classAttr,
                    EffectiveTriggerSignal = triggerSignal,
                    EffectivePriority = attr.Priority != 0 ? attr.Priority : (classAttr?.DefaultPriority ?? 0),
                    EffectiveMaxConcurrency = attr.MaxConcurrency != 1 ? attr.MaxConcurrency : (classAttr?.DefaultMaxConcurrency ?? 1),
                    Lane = laneName,
                    LaneMaxConcurrency = laneConcurrency
                });
            }
        }

        return descriptors.OrderBy(d => d.EffectivePriority).ToList();
    }

    private static (string name, int maxConcurrency) ParseLane(string? lane)
    {
        if (string.IsNullOrEmpty(lane))
            return ("default", 0);

        var colonIndex = lane.IndexOf(':');
        if (colonIndex < 0)
            return (lane, 0);

        var name = lane.Substring(0, colonIndex);
        var concurrencyStr = lane.Substring(colonIndex + 1);
        return int.TryParse(concurrencyStr, out var concurrency)
            ? (name, concurrency)
            : (lane, 0);
    }

    private void InitializeGates()
    {
        // Per-job gates
        foreach (var job in _jobs)
        {
            var key = $"{job.JobType.FullName}.{job.Method.Name}";
            if (job.EffectiveMaxConcurrency > 0)
                _jobGates[key] = new SemaphoreSlim(job.EffectiveMaxConcurrency, job.EffectiveMaxConcurrency);
        }

        // Per-lane gates
        var laneGroups = _jobs.GroupBy(j => j.Lane).ToList();
        foreach (var group in laneGroups)
        {
            var explicitConcurrency = group.Max(j => j.LaneMaxConcurrency);
            var laneConcurrency = explicitConcurrency > 0
                ? explicitConcurrency
                : group.Sum(j => j.EffectiveMaxConcurrency > 0 ? j.EffectiveMaxConcurrency : 1);
            _laneGates[group.Key] = new SemaphoreSlim(laneConcurrency, laneConcurrency);
        }
    }

    private void InitializeCoordinator(EphemeralOptions? options)
    {
        var opts = options ?? new EphemeralOptions();
        opts = new EphemeralOptions
        {
            MaxConcurrency = opts.MaxConcurrency > 0 ? opts.MaxConcurrency : Math.Max(1, _jobs.Count),
            Signals = _signals,
            MaxConcurrencyPerKey = 1,
            EnableFairScheduling = true,
            MaxTrackedOperations = opts.MaxTrackedOperations,
            MaxOperationLifetime = opts.MaxOperationLifetime,
            OnSignal = opts.OnSignal,
            OnSignalAsync = opts.OnSignalAsync,
            SignalConstraints = opts.SignalConstraints
        };

        _coordinator = new EphemeralKeyedWorkCoordinator<ScopedJobInvocation, string>(
            work => $"{work.Descriptor.Lane}:{work.Descriptor.ExtractKey(work.Signal) ?? "default"}",
            async (work, ct) => await ExecuteJobInScopeAsync(work, ct).ConfigureAwait(false),
            opts);
    }

    private void OnSignal(SignalEvent signal)
    {
        foreach (var job in _jobs)
        {
            if (!job.Matches(signal))
                continue;

            var invocation = new ScopedJobInvocation(job, signal, null);
            _ = _coordinator.EnqueueAsync(invocation, _cts.Token);
        }
    }

    /// <summary>
    /// Executes a job within a fresh DI scope.
    /// </summary>
    private async Task ExecuteJobInScopeAsync(ScopedJobInvocation work, CancellationToken ct)
    {
        var job = work.Descriptor;
        var attr = job.Attribute;
        var jobKey = $"{job.JobType.FullName}.{job.Method.Name}";

        // Acquire gates
        SemaphoreSlim? laneGate = null;
        if (_laneGates.TryGetValue(job.Lane, out laneGate))
            await laneGate.WaitAsync(ct).ConfigureAwait(false);

        SemaphoreSlim? jobGate = null;
        if (_jobGates.TryGetValue(jobKey, out jobGate))
            await jobGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Wait for prerequisite signals
            if (job.AwaitSignals is { Count: > 0 } awaitSignals)
            {
                await WaitForSignalsAsync(awaitSignals, job.AwaitTimeout, ct).ConfigureAwait(false);
            }

            // Emit start signals
            if (attr.EmitOnStart is { Length: > 0 } startSignals)
            {
                foreach (var sig in startSignals)
                    _signals.Raise(sig, work.Signal.Key);
            }

            var attempts = 0;
            var maxAttempts = Math.Max(1, attr.MaxRetries + 1);
            Exception? lastError = null;

            while (attempts < maxAttempts)
            {
                attempts++;

                // CREATE A NEW SCOPE FOR EACH ATTEMPT
                await using var scope = _serviceProvider.CreateAsyncScope();

                try
                {
                    // Resolve the job instance from the scope
                    var jobInstance = ResolveJobInstance(scope.ServiceProvider, job.JobType);

                    // Build method arguments
                    var args = BuildMethodArguments(job.Method, work.Signal, work.Payload, ct);

                    // Apply timeout if specified
                    var effectiveCt = ct;
                    CancellationTokenSource? timeoutCts = null;

                    if (job.Timeout.HasValue)
                    {
                        timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(job.Timeout.Value);
                        effectiveCt = timeoutCts.Token;

                        // Update CancellationToken in args
                        for (var i = 0; i < args.Length; i++)
                        {
                            if (args[i] is CancellationToken)
                                args[i] = effectiveCt;
                        }
                    }

                    try
                    {
                        // Invoke the method
                        var result = job.Method.Invoke(jobInstance, args);

                        // Await if async
                        if (result is Task task)
                            await task.ConfigureAwait(false);
                        else if (result is ValueTask valueTask)
                            await valueTask.ConfigureAwait(false);

                        // Success - emit completion signals
                        if (attr.EmitOnComplete is { Length: > 0 } completeSignals)
                        {
                            foreach (var sig in completeSignals)
                                _signals.Raise(sig, work.Signal.Key);
                        }

                        return;
                    }
                    finally
                    {
                        timeoutCts?.Dispose();
                    }
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    // Unwrap TargetInvocationException
                    if (ex.InnerException is OperationCanceledException && ct.IsCancellationRequested)
                        throw ex.InnerException;

                    lastError = ex.InnerException;

                    if (attempts < maxAttempts)
                    {
                        var delay = attr.RetryDelayMs * (int)Math.Pow(2, attempts - 1);
                        _signals.Raise($"job.retry:{job.Method.Name}:{attempts}", work.Signal.Key);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    if (attempts < maxAttempts)
                    {
                        var delay = attr.RetryDelayMs * (int)Math.Pow(2, attempts - 1);
                        _signals.Raise($"job.retry:{job.Method.Name}:{attempts}", work.Signal.Key);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }

                // Scope is disposed here, releasing scoped services
            }

            // All retries exhausted
            if (lastError != null)
            {
                if (attr.EmitOnFailure is { Length: > 0 } failSignals)
                {
                    foreach (var sig in failSignals)
                        _signals.Raise(sig, work.Signal.Key);
                }

                _signals.Raise($"job.failed:{job.Method.Name}:{lastError.GetType().Name}", work.Signal.Key);

                if (!attr.SwallowExceptions)
                    throw lastError;
            }
        }
        finally
        {
            jobGate?.Release();
            laneGate?.Release();
        }
    }

    private object ResolveJobInstance(IServiceProvider scopedProvider, Type jobType)
    {
        // Try to resolve from DI first
        var service = scopedProvider.GetService(jobType);
        if (service != null)
            return service;

        // Fall back to ActivatorUtilities for constructor injection
        return ActivatorUtilities.CreateInstance(scopedProvider, jobType);
    }

    private object?[] BuildMethodArguments(MethodInfo method, SignalEvent signal, object? payload, CancellationToken ct)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];

            if (param.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
            }
            else if (param.ParameterType == typeof(SignalEvent))
            {
                args[i] = signal;
            }
            else if (payload != null && param.ParameterType.IsInstanceOfType(payload))
            {
                args[i] = payload;
            }
            else if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot resolve parameter '{param.Name}' of type {param.ParameterType.Name}");
            }
        }

        return args;
    }

    private async Task WaitForSignalsAsync(IReadOnlyList<string> patterns, TimeSpan? timeout, CancellationToken ct)
    {
        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (timeoutCts != null)
            timeoutCts.CancelAfter(timeout!.Value);

        var effectiveCt = timeoutCts?.Token ?? ct;
        var pollInterval = TimeSpan.FromMilliseconds(50);

        while (!effectiveCt.IsCancellationRequested)
        {
            var allFound = patterns.All(pattern =>
                _signals.Detect(s => StringPatternMatcher.Matches(s.Signal, pattern)));

            if (allFound) return;

            try
            {
                await Task.Delay(pollInterval, effectiveCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.HasValue && timeoutCts!.Token.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for signals: {string.Join(", ", patterns)}");
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        _signals.SignalRaised -= OnSignal;
        _cts.Cancel();
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();

        foreach (var gate in _jobGates.Values)
            gate.Dispose();
        _jobGates.Clear();

        foreach (var gate in _laneGates.Values)
            gate.Dispose();
        _laneGates.Clear();
    }

    private sealed record ScopedJobInvocation(ScopedJobDescriptor Descriptor, SignalEvent Signal, object? Payload);
}
