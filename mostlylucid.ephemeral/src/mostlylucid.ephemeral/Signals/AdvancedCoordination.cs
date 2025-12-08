using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral;

#region Typed Signal Keys

/// <summary>
///     Compile-time safe signal key. Use static readonly instances to avoid magic strings.
/// </summary>
/// <example>
///     public static class Signals
///     {
///     public static readonly SignalKey&lt;double&gt; BotScore = new("bot.score");
///     public static readonly SignalKey&lt;string&gt; UserAgent = new("user.agent");
///     }
/// </example>
public readonly record struct SignalKey<TPayload>(string Name)
{
    public static implicit operator string(SignalKey<TPayload> key)
    {
        return key.Name;
    }

    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
///     Non-generic signal key for untyped signals.
/// </summary>
public readonly record struct SignalKey(string Name)
{
    public static implicit operator string(SignalKey key)
    {
        return key.Name;
    }

    public override string ToString()
    {
        return Name;
    }

    /// <summary>
    ///     Create a typed key from this key.
    /// </summary>
    public SignalKey<TPayload> WithPayload<TPayload>()
    {
        return new SignalKey<TPayload>(Name);
    }
}

/// <summary>
///     Extension methods for typed signal keys.
/// </summary>
public static class SignalKeyExtensions
{
    /// <summary>
    ///     Raise a typed signal using a SignalKey.
    /// </summary>
    public static void Raise<TPayload>(this TypedSignalSink<TPayload> sink, SignalKey<TPayload> key, TPayload payload,
        string? opKey = null)
    {
        sink.Raise(key.Name, payload, opKey);
    }

    /// <summary>
    ///     Sense signals matching a typed key.
    /// </summary>
    public static IReadOnlyList<SignalEvent<TPayload>> Sense<TPayload>(this TypedSignalSink<TPayload> sink,
        SignalKey<TPayload> key)
    {
        return sink.Sense(e => e.Signal == key.Name);
    }

    /// <summary>
    ///     Raise an untyped signal using a SignalKey.
    /// </summary>
    public static void Raise(this SignalSink sink, SignalKey key, string? opKey = null)
    {
        sink.Raise(key.Name, opKey);
    }
}

#endregion

#region Signal Aggregation Window

/// <summary>
///     Time-windowed signal aggregation for queries like "sum of scores in last 5 minutes".
/// </summary>
public sealed class SignalAggregationWindow<TPayload>
{
    private readonly TimeSpan _defaultWindow;
    private readonly TypedSignalSink<TPayload> _sink;

    public SignalAggregationWindow(TypedSignalSink<TPayload> sink, TimeSpan? defaultWindow = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _defaultWindow = defaultWindow ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    ///     Query signals within a time window.
    /// </summary>
    public IReadOnlyList<SignalEvent<TPayload>> Query(string? pattern = null, TimeSpan? window = null)
    {
        var cutoff = DateTimeOffset.UtcNow - (window ?? _defaultWindow);
        return _sink.Sense(e =>
            e.Timestamp >= cutoff &&
            (pattern == null || StringPatternMatcher.Matches(e.Signal, pattern)));
    }

    /// <summary>
    ///     Count signals matching pattern within window.
    /// </summary>
    public int Count(string? pattern = null, TimeSpan? window = null)
    {
        return Query(pattern, window).Count;
    }

    /// <summary>
    ///     Sum payloads within window using a selector.
    /// </summary>
    public double Sum(Func<TPayload, double> selector, string? pattern = null, TimeSpan? window = null)
    {
        return Query(pattern, window).Sum(e => selector(e.Payload));
    }

    /// <summary>
    ///     Average payloads within window using a selector.
    /// </summary>
    public double Average(Func<TPayload, double> selector, string? pattern = null, TimeSpan? window = null)
    {
        var items = Query(pattern, window);
        return items.Count == 0 ? 0 : items.Average(e => selector(e.Payload));
    }

    /// <summary>
    ///     Group signals by a key selector within window.
    /// </summary>
    public IEnumerable<IGrouping<TKey, SignalEvent<TPayload>>> GroupBy<TKey>(
        Func<SignalEvent<TPayload>, TKey> keySelector,
        string? pattern = null,
        TimeSpan? window = null)
    {
        return Query(pattern, window).GroupBy(keySelector);
    }
}

#endregion

#region Early Exit Coordinator

/// <summary>
///     Options for early exit behavior.
/// </summary>
public sealed class EarlyExitOptions<TInput, TResult>
{
    /// <summary>
    ///     Signal patterns that trigger early exit.
    ///     Example: ["verdict.confirmed_bot", "verdict.verified_human"]
    /// </summary>
    public IReadOnlySet<string>? EarlyExitSignals { get; init; }

    /// <summary>
    ///     Factory to produce a result from partial results when early exit is triggered.
    ///     Receives the triggering signal and all results collected so far.
    /// </summary>
    public Func<string, IReadOnlyList<TResult>, TResult>? OnEarlyExit { get; init; }
}

/// <summary>
///     Result coordinator with early exit capability.
///     When an early exit signal is detected, remaining operations are cancelled and partial results are aggregated.
/// </summary>
public sealed class EarlyExitResultCoordinator<TInput, TResult> : IAsyncDisposable
{
    private readonly CancellationTokenSource _exitCts = new();
    private readonly EarlyExitOptions<TInput, TResult> _exitOptions;
    private readonly TaskCompletionSource<EarlyExitTcsResult<TResult>> _exitTcs = new();
    private readonly EphemeralResultCoordinator<TInput, TResult> _inner;
    private readonly SignalSink _sink;
    private volatile bool _earlyExited;

    public EarlyExitResultCoordinator(
        Func<TInput, CancellationToken, Task<TResult>> body,
        EarlyExitOptions<TInput, TResult> exitOptions,
        EphemeralOptions? options = null)
    {
        _exitOptions = exitOptions ?? throw new ArgumentNullException(nameof(exitOptions));
        _sink = options?.Signals ?? new SignalSink();

        var opts = options ?? new EphemeralOptions();
        if (opts.Signals == null)
            opts = new EphemeralOptions
            {
                MaxConcurrency = opts.MaxConcurrency,
                EnableDynamicConcurrency = opts.EnableDynamicConcurrency,
                MaxTrackedOperations = opts.MaxTrackedOperations,
                MaxOperationLifetime = opts.MaxOperationLifetime,
                OnSample = opts.OnSample,
                MaxConcurrencyPerKey = opts.MaxConcurrencyPerKey,
                EnableFairScheduling = opts.EnableFairScheduling,
                FairSchedulingThreshold = opts.FairSchedulingThreshold,
                Signals = _sink,
                OnSignal = opts.OnSignal,
                OnSignalAsync = opts.OnSignalAsync,
                OnSignalRetracted = opts.OnSignalRetracted,
                OnSignalRetractedAsync = opts.OnSignalRetractedAsync,
                MaxConcurrentSignalHandlers = opts.MaxConcurrentSignalHandlers,
                MaxQueuedSignals = opts.MaxQueuedSignals,
                Emits = opts.Emits,
                Listens = opts.Listens,
                SignalConstraints = opts.SignalConstraints,
                CancelOnSignals = opts.CancelOnSignals,
                DeferOnSignals = opts.DeferOnSignals,
                DeferCheckInterval = opts.DeferCheckInterval,
                MaxDeferAttempts = opts.MaxDeferAttempts
            };

        _inner = new EphemeralResultCoordinator<TInput, TResult>(body, opts);
        _sink.SignalRaised += OnSignal;
    }

    public int PendingCount => _inner.PendingCount;
    public int ActiveCount => _inner.ActiveCount;
    public int TotalEnqueued => _inner.TotalEnqueued;
    public int TotalCompleted => _inner.TotalCompleted;
    public bool EarlyExited => _earlyExited;
    public string? ExitSignal { get; private set; }

    public async ValueTask DisposeAsync()
    {
        _sink.SignalRaised -= OnSignal;
        _exitCts.Cancel();
        _exitCts.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSignal(SignalEvent evt)
    {
        if (_earlyExited || _exitOptions.EarlyExitSignals is not { Count: > 0 })
            return;

        if (StringPatternMatcher.MatchesAny(evt.Signal, _exitOptions.EarlyExitSignals))
        {
            _earlyExited = true;
            ExitSignal = evt.Signal;
            _exitCts.Cancel();

            var partialResults = _inner.GetResults();
            TResult finalResult;
            if (_exitOptions.OnEarlyExit != null)
                finalResult = _exitOptions.OnEarlyExit(evt.Signal, partialResults.ToArray());
            else
                finalResult = default!;
            _exitTcs.TrySetResult(new EarlyExitTcsResult<TResult>(true, evt.Signal, finalResult));

            _sink.Raise($"coordinator.early_exit:{evt.Signal}", evt.Key);
        }
    }

    public async ValueTask EnqueueAsync(TInput item, CancellationToken ct = default)
    {
        if (_earlyExited) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _exitCts.Token);
        try
        {
            await _inner.EnqueueAsync(item, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_earlyExited)
        {
            // Expected - early exit triggered
        }
    }

    public void Complete()
    {
        _inner.Complete();
    }

    public async Task<EarlyExitResult<TResult>> DrainAsync(CancellationToken ct = default)
    {
        _inner.Complete();

        var drainTask = _inner.DrainAsync(ct);
        var exitTask = _exitTcs.Task;

        var completed = await Task.WhenAny(drainTask, exitTask).ConfigureAwait(false);

        if (_earlyExited)
        {
            var exitResult = await exitTask.ConfigureAwait(false);
            return new EarlyExitResult<TResult>(
                true,
                exitResult.Signal,
                exitResult.Result,
                _inner.GetResults().ToArray());
        }

        await drainTask.ConfigureAwait(false);
        return new EarlyExitResult<TResult>(
            false,
            null,
            default!,
            _inner.GetResults().ToArray());
    }

    public IReadOnlyCollection<TResult> GetResults()
    {
        return _inner.GetResults();
    }
}

public readonly record struct EarlyExitResult<TResult>(
    bool Exited,
    string? ExitSignal,
    TResult AggregatedResult,
    IReadOnlyList<TResult> PartialResults);

internal readonly record struct EarlyExitTcsResult<TResult>(
    bool Exited,
    string? Signal,
    TResult Result);

#endregion

#region Contributor Tracking (ExpectContributors)

/// <summary>
///     Tracks named contributors and their completion status for quorum-based coordination.
/// </summary>
public sealed class ContributorTracker<TResult>
{
    private readonly ConcurrentDictionary<string, ContributorState<TResult>> _contributors = new();
    private readonly TaskCompletionSource<QuorumContributorResult<TResult>> _quorumTcs = new();
    private int _completedCount;
    private int _requiredQuorum;

    public ContributorTracker(IEnumerable<string> expectedContributors, SignalSink? sink = null)
    {
        Sink = sink ?? new SignalSink();
        foreach (var name in expectedContributors)
            _contributors[name] = new ContributorState<TResult>(name);
    }

    public SignalSink Sink { get; }

    public int ExpectedCount => _contributors.Count;
    public int CompletedCount => Volatile.Read(ref _completedCount);
    public bool QuorumReached { get; private set; }

    /// <summary>
    ///     Mark a contributor as completed with a result.
    /// </summary>
    public void Complete(string contributor, TResult result)
    {
        if (!_contributors.TryGetValue(contributor, out var state))
            throw new ArgumentException($"Unknown contributor: {contributor}", nameof(contributor));

        if (state.IsCompleted)
            return;

        state.Result = result;
        state.IsCompleted = true;
        state.CompletedAt = DateTimeOffset.UtcNow;
        var count = Interlocked.Increment(ref _completedCount);

        Sink.Raise($"contributor.completed:{contributor}", contributor);

        if (_requiredQuorum > 0 && count >= _requiredQuorum && !QuorumReached)
        {
            QuorumReached = true;
            _quorumTcs.TrySetResult(BuildQuorumResult());
        }
    }

    /// <summary>
    ///     Mark a contributor as failed.
    /// </summary>
    public void Fail(string contributor, Exception? error = null)
    {
        if (!_contributors.TryGetValue(contributor, out var state))
            return;

        state.IsFailed = true;
        state.Error = error;
        Sink.Raise($"contributor.failed:{contributor}", contributor);
    }

    /// <summary>
    ///     Wait for a quorum of contributors to complete.
    /// </summary>
    public async Task<QuorumContributorResult<TResult>> WaitForQuorumAsync(
        int minCompleted,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (minCompleted <= 0 || minCompleted > _contributors.Count)
            throw new ArgumentOutOfRangeException(nameof(minCompleted));

        _requiredQuorum = minCompleted;

        // Check if already reached
        if (CompletedCount >= minCompleted)
        {
            QuorumReached = true;
            return BuildQuorumResult();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var registration = timeoutCts.Token.Register(() => { _quorumTcs.TrySetResult(BuildQuorumResult()); });

        return await _quorumTcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    ///     Get all completed results.
    /// </summary>
    public IReadOnlyList<(string Contributor, TResult Result)> GetCompletedResults()
    {
        return _contributors.Values
            .Where(s => s.IsCompleted && s.Result != null)
            .Select(s => (s.Name, s.Result!))
            .ToArray();
    }

    /// <summary>
    ///     Get the result for a specific contributor.
    /// </summary>
    public bool TryGetResult(string contributor, out TResult? result)
    {
        if (_contributors.TryGetValue(contributor, out var state) && state.IsCompleted)
        {
            result = state.Result;
            return true;
        }

        result = default;
        return false;
    }

    private QuorumContributorResult<TResult> BuildQuorumResult()
    {
        var completed = _contributors.Values.Where(s => s.IsCompleted).ToArray();
        var failed = _contributors.Values.Where(s => s.IsFailed).ToArray();
        var pending = _contributors.Values.Where(s => !s.IsCompleted && !s.IsFailed).ToArray();

        return new QuorumContributorResult<TResult>(
            completed.Length >= _requiredQuorum,
            completed.Length,
            failed.Length,
            pending.Length,
            completed.Where(s => s.Result != null).Select(s => (s.Name, s.Result!)).ToArray(),
            !QuorumReached && completed.Length < _requiredQuorum
        );
    }

    private sealed class ContributorState<T>
    {
        public ContributorState(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public T? Result { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsFailed { get; set; }
        public Exception? Error { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }
}

public readonly record struct QuorumContributorResult<TResult>(
    bool Reached,
    int CompletedCount,
    int FailedCount,
    int PendingCount,
    IReadOnlyList<(string Contributor, TResult Result)> Results,
    bool TimedOut);

#endregion

#region Operation Dependencies (Topological Execution)

/// <summary>
///     Definition of an operation with dependencies.
/// </summary>
public sealed record DependentOperation<T>(
    string Name,
    T Item,
    IReadOnlyCollection<string>? DependsOn = null);

/// <summary>
///     Executes operations respecting dependency order (topological sort).
///     Operations only start after all their dependencies complete successfully.
/// </summary>
public sealed class DependencyCoordinator<T> : IAsyncDisposable
{
    private readonly Func<T, CancellationToken, Task> _body;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxConcurrency;
    private readonly ConcurrentDictionary<string, OperationNode> _operations = new();
    private readonly List<Task> _runningTasks = new();

    public DependencyCoordinator(
        Func<T, CancellationToken, Task> body,
        int maxConcurrency = 0,
        SignalSink? sink = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : Environment.ProcessorCount;
        _concurrencyGate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        Sink = sink ?? new SignalSink();
    }

    public SignalSink Sink { get; }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        _concurrencyGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Add an operation with optional dependencies.
    /// </summary>
    public void AddOperation(string name, T item, params string[] dependsOn)
    {
        AddOperation(new DependentOperation<T>(name, item, dependsOn));
    }

    /// <summary>
    ///     Add an operation with optional dependencies.
    /// </summary>
    public void AddOperation(DependentOperation<T> operation)
    {
        var node = new OperationNode(operation);
        if (!_operations.TryAdd(operation.Name, node))
            throw new ArgumentException($"Operation '{operation.Name}' already exists.");
    }

    /// <summary>
    ///     Execute all operations respecting dependency order.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        // Validate dependencies
        foreach (var (name, node) in _operations)
            if (node.Operation.DependsOn is { Count: > 0 } deps)
                foreach (var dep in deps)
                    if (!_operations.ContainsKey(dep))
                        throw new InvalidOperationException(
                            $"Operation '{name}' depends on unknown operation '{dep}'.");

        // Check for cycles
        if (HasCycle())
            throw new InvalidOperationException("Circular dependency detected.");

        // Start operations with no dependencies
        foreach (var (_, node) in _operations)
            if (node.Operation.DependsOn is not { Count: > 0 })
            {
                var task = RunOperationAsync(node, linked.Token);
                lock (_runningTasks)
                {
                    _runningTasks.Add(task);
                }
            }

        // Wait for all to complete
        while (true)
        {
            Task[] tasks;
            lock (_runningTasks)
            {
                tasks = _runningTasks.Where(t => !t.IsCompleted).ToArray();
            }

            if (tasks.Length == 0) break;
            await Task.WhenAny(tasks).ConfigureAwait(false);
        }

        // Check for failures
        var failed = _operations.Values.Where(n => n.IsFailed).ToArray();
        if (failed.Length > 0)
        {
            var errors = failed.Where(n => n.Error != null).Select(n => n.Error!).ToArray();
            if (errors.Length == 1)
                throw errors[0];
            if (errors.Length > 1)
                throw new AggregateException("One or more operations failed.", errors);
        }
    }

    private async Task RunOperationAsync(OperationNode node, CancellationToken ct)
    {
        // Wait for dependencies
        if (node.Operation.DependsOn is { Count: > 0 } deps)
            foreach (var depName in deps)
                if (_operations.TryGetValue(depName, out var depNode))
                {
                    await depNode.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
                    if (depNode.IsFailed)
                    {
                        node.IsFailed = true;
                        node.Error = new InvalidOperationException($"Dependency '{depName}' failed.");
                        node.Completion.TrySetException(node.Error);
                        Sink.Raise($"operation.skipped:{node.Operation.Name}", node.Operation.Name);
                        return;
                    }
                }

        await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Sink.Raise($"operation.start:{node.Operation.Name}", node.Operation.Name);
            await _body(node.Operation.Item, ct).ConfigureAwait(false);
            node.IsCompleted = true;
            node.Completion.TrySetResult();
            Sink.Raise($"operation.done:{node.Operation.Name}", node.Operation.Name);

            // Trigger dependents
            foreach (var (_, other) in _operations)
            {
                if (other.IsCompleted || other.IsFailed) continue;
                if (other.Operation.DependsOn?.Contains(node.Operation.Name) == true)
                    if (AllDependenciesComplete(other))
                    {
                        var task = RunOperationAsync(other, ct);
                        lock (_runningTasks)
                        {
                            _runningTasks.Add(task);
                        }
                    }
            }
        }
        catch (Exception ex)
        {
            node.IsFailed = true;
            node.Error = ex;
            node.Completion.TrySetException(ex);
            Sink.Raise($"operation.failed:{node.Operation.Name}:{ex.GetType().Name}", node.Operation.Name);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private bool AllDependenciesComplete(OperationNode node)
    {
        if (node.Operation.DependsOn is not { Count: > 0 } deps)
            return true;

        foreach (var dep in deps)
            if (!_operations.TryGetValue(dep, out var depNode) || !depNode.IsCompleted)
                return false;
        return true;
    }

    private bool HasCycle()
    {
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();

        foreach (var name in _operations.Keys)
            if (HasCycleDfs(name, visited, inStack))
                return true;
        return false;
    }

    private bool HasCycleDfs(string name, HashSet<string> visited, HashSet<string> inStack)
    {
        if (inStack.Contains(name)) return true;
        if (visited.Contains(name)) return false;

        visited.Add(name);
        inStack.Add(name);

        if (_operations.TryGetValue(name, out var node) && node.Operation.DependsOn is { Count: > 0 } deps)
            foreach (var dep in deps)
                if (HasCycleDfs(dep, visited, inStack))
                    return true;

        inStack.Remove(name);
        return false;
    }

    private sealed class OperationNode
    {
        public OperationNode(DependentOperation<T> operation)
        {
            Operation = operation;
        }

        public DependentOperation<T> Operation { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCompleted { get; set; }
        public bool IsFailed { get; set; }
        public Exception? Error { get; set; }
    }
}

#endregion

#region Staged Execution (Simplified API)

/// <summary>
///     Fluent builder for staged/wave execution.
/// </summary>
public sealed class StagedPipelineBuilder<T>
{
    private readonly List<StageDefinition> _stages = new();

    public StagedPipelineBuilder(SignalSink? sink = null)
    {
        Sink = sink ?? new SignalSink();
    }

    public SignalSink Sink { get; }

    /// <summary>
    ///     Add a stage that runs immediately (no trigger).
    /// </summary>
    public StagedPipelineBuilder<T> AddStage(int order, Func<T, CancellationToken, Task> work, string? name = null)
    {
        _stages.Add(new StageDefinition(order, name ?? $"stage-{order}", null, work));
        return this;
    }

    /// <summary>
    ///     Add a stage that triggers when any of the specified signals are seen.
    /// </summary>
    public StagedPipelineBuilder<T> AddStage(int order, Func<T, CancellationToken, Task> work,
        IEnumerable<string> whenAny, string? name = null)
    {
        _stages.Add(new StageDefinition(order, name ?? $"stage-{order}", whenAny.ToArray(), work));
        return this;
    }

    /// <summary>
    ///     Execute all stages for the given items.
    /// </summary>
    public async Task ExecuteAsync(IEnumerable<T> items, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var ordered = _stages.OrderBy(s => s.Order).ToList();

        foreach (var stage in ordered)
        {
            // Wait for trigger signals if any
            if (stage.TriggerPatterns is { Length: > 0 })
            {
                var triggered = false;
                while (!triggered && !ct.IsCancellationRequested)
                {
                    foreach (var pattern in stage.TriggerPatterns)
                        if (Sink.Detect(s => StringPatternMatcher.Matches(s.Signal, pattern)))
                        {
                            triggered = true;
                            break;
                        }

                    if (!triggered)
                        await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }

            Sink.Raise($"stage.start:{stage.Name}", stage.Name);

            // Execute stage for all items
            await itemList.EphemeralForEachAsync(
                stage.Work,
                new EphemeralOptions { Signals = Sink },
                ct).ConfigureAwait(false);

            Sink.Raise($"stage.complete:{stage.Name}", stage.Name);
        }
    }

    private sealed record StageDefinition(
        int Order,
        string Name,
        string[]? TriggerPatterns,
        Func<T, CancellationToken, Task> Work);
}

#endregion