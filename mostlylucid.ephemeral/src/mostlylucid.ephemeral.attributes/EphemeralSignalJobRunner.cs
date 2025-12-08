namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
///     Listens for signals and enqueues attributed jobs on a background coordinator.
///     Jobs can specify a Lane to control concurrency grouping - jobs in the same lane
///     share concurrency gates while still being able to interact via signals.
/// </summary>
public sealed class EphemeralSignalJobRunner : IAsyncDisposable
{
    private readonly EphemeralKeyedWorkCoordinator<EphemeralJobInvocation, string> _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, SemaphoreSlim> _jobGates = new();
    private readonly Dictionary<string, SemaphoreSlim> _laneGates = new();
    private readonly SignalSink _signals;

    public EphemeralSignalJobRunner(SignalSink signals, IEnumerable<object> jobTargets,
        EphemeralOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        if (jobTargets is null) throw new ArgumentNullException(nameof(jobTargets));

        Jobs = EphemeralJobScanner.ScanAll(jobTargets);
        if (Jobs.Count == 0)
            throw new ArgumentException("No attributed jobs were discovered.", nameof(jobTargets));

        // Create per-job concurrency gates (for MaxConcurrency on individual jobs)
        foreach (var job in Jobs)
        {
            var key = $"{job.Target.GetType().FullName}.{job.Method.Name}";
            if (job.EffectiveMaxConcurrency > 0)
                _jobGates[key] = new SemaphoreSlim(job.EffectiveMaxConcurrency, job.EffectiveMaxConcurrency);
        }

        // Create per-lane concurrency gates (for lane-based concurrency control)
        // Use explicit LaneMaxConcurrency if set (highest wins), otherwise sum of job concurrencies
        var laneGroups = Jobs.GroupBy(j => j.Lane).ToList();
        foreach (var group in laneGroups)
        {
            var explicitConcurrency = group.Max(j => j.LaneMaxConcurrency);
            var laneConcurrency = explicitConcurrency > 0
                ? explicitConcurrency
                : group.Sum(j => j.EffectiveMaxConcurrency > 0 ? j.EffectiveMaxConcurrency : 1);
            _laneGates[group.Key] = new SemaphoreSlim(laneConcurrency, laneConcurrency);
        }

        var opts = options ?? new EphemeralOptions();
        opts = new EphemeralOptions
        {
            MaxConcurrency = opts.MaxConcurrency > 0 ? opts.MaxConcurrency : Math.Max(1, Jobs.Count),
            Signals = _signals,
            MaxConcurrencyPerKey = 1, // Sequential per key for ordering
            EnableFairScheduling = true,
            MaxTrackedOperations = opts.MaxTrackedOperations,
            MaxOperationLifetime = opts.MaxOperationLifetime,
            OnSignal = opts.OnSignal,
            OnSignalAsync = opts.OnSignalAsync,
            SignalConstraints = opts.SignalConstraints
        };

        // Key by lane + extracted key to allow per-lane ordering
        _coordinator = new EphemeralKeyedWorkCoordinator<EphemeralJobInvocation, string>(
            work => $"{work.Descriptor.Lane}:{work.Descriptor.ExtractKey(work.Signal, work.Payload) ?? "default"}",
            async (work, ct) => await ExecuteJobAsync(work, ct).ConfigureAwait(false),
            opts);

        _signals.SignalRaised += OnSignal;
    }

    public int PendingCount => _coordinator.PendingCount;
    public int ActiveCount => _coordinator.ActiveCount;
    public IReadOnlyList<EphemeralJobDescriptor> Jobs { get; }

    public IReadOnlyCollection<string> Lanes => _laneGates.Keys;

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

    private void OnSignal(SignalEvent signal)
    {
        foreach (var job in Jobs)
        {
            if (!job.Matches(signal))
                continue;

            var invocation = new EphemeralJobInvocation(job, signal, null);
            _ = _coordinator.EnqueueAsync(invocation, _cts.Token);
        }
    }

    private async Task ExecuteJobAsync(EphemeralJobInvocation work, CancellationToken ct)
    {
        var job = work.Descriptor;
        var attr = job.Attribute;
        var jobKey = $"{job.Target.GetType().FullName}.{job.Method.Name}";

        // Acquire lane gate first (controls lane-level concurrency)
        SemaphoreSlim? laneGate = null;
        if (_laneGates.TryGetValue(job.Lane, out laneGate))
            await laneGate.WaitAsync(ct).ConfigureAwait(false);

        // Acquire per-job concurrency gate (controls job-level concurrency within the lane)
        SemaphoreSlim? jobGate = null;
        if (_jobGates.TryGetValue(jobKey, out jobGate))
            await jobGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Wait for prerequisite signals if specified
            if (job.AwaitSignals is { Count: > 0 } awaitSignals)
                await WaitForSignalsAsync(awaitSignals, job.AwaitTimeout, ct).ConfigureAwait(false);

            // Emit start signals
            if (attr.EmitOnStart is { Length: > 0 } startSignals)
                foreach (var sig in startSignals)
                    _signals.Raise(sig, work.Signal.Key);

            var attempts = 0;
            var maxAttempts = Math.Max(1, attr.MaxRetries + 1);
            Exception? lastError = null;

            while (attempts < maxAttempts)
            {
                attempts++;
                try
                {
                    // Apply timeout if specified
                    if (job.Timeout.HasValue)
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(job.Timeout.Value);
                        await job.InvokeAsync(timeoutCts.Token, work.Signal, work.Payload).ConfigureAwait(false);
                    }
                    else
                    {
                        await job.InvokeAsync(ct, work.Signal, work.Payload).ConfigureAwait(false);
                    }

                    // Success - emit completion signals
                    if (attr.EmitOnComplete is { Length: > 0 } completeSignals)
                        foreach (var sig in completeSignals)
                            _signals.Raise(sig, work.Signal.Key);

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // Don't retry on external cancellation
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    if (attempts < maxAttempts)
                    {
                        // Exponential backoff
                        var delay = attr.RetryDelayMs * (int)Math.Pow(2, attempts - 1);
                        _signals.Raise($"job.retry:{job.Method.Name}:{attempts}", work.Signal.Key);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }
            }

            // All retries exhausted
            if (lastError != null)
            {
                // Emit failure signals
                if (attr.EmitOnFailure is { Length: > 0 } failSignals)
                    foreach (var sig in failSignals)
                        _signals.Raise(sig, work.Signal.Key);

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

    /// <summary>
    ///     Wait for all prerequisite signals to be present in the sink.
    /// </summary>
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
            var allFound = true;
            foreach (var pattern in patterns)
                if (!_signals.Detect(s => StringPatternMatcher.Matches(s.Signal, pattern)))
                {
                    allFound = false;
                    break;
                }

            if (allFound)
                return;

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

    private sealed record EphemeralJobInvocation(
        EphemeralJobDescriptor Descriptor,
        SignalEvent Signal,
        object? Payload);
}