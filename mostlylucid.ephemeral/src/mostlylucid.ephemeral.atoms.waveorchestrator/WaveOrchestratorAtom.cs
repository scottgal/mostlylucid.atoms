using System.Collections.Concurrent;
using System.Diagnostics;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.WaveOrchestrator;

/// <summary>
///     Wave-based parallel orchestrator with circuit breaker and early exit.
///
///     Executes workers in configurable waves:
///     - Workers in the same wave run in parallel (up to maxParallelPerWave)
///     - Waves execute sequentially
///     - Early exit when earlyExitCondition is met
///     - Circuit breaker per worker prevents cascading failures
///
///     Inspired by BotDetection blackboard orchestrator pattern.
/// </summary>
public class WaveOrchestratorAtom<TInput, TOutput> : IAsyncDisposable
{
    private readonly IReadOnlyList<WaveWorker<TInput, TOutput>> _workers;
    private readonly WaveOrchestratorOptions<TOutput> _options;
    private readonly SignalSink? _signals;
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuitBreakers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WaveOrchestratorAtom(
        IEnumerable<WaveWorker<TInput, TOutput>> workers,
        WaveOrchestratorOptions<TOutput>? options = null,
        SignalSink? signals = null)
    {
        _workers = workers.OrderBy(w => w.Wave).ThenBy(w => w.Priority).ToList();
        _options = options ?? new WaveOrchestratorOptions<TOutput>();
        _signals = signals;
    }

    /// <summary>
    ///     Execute all waves until completion or early exit.
    /// </summary>
    public async Task<WaveOrchestratorResult<TOutput>> ExecuteAsync(
        TInput input,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<TOutput>();
        var completedWorkers = new HashSet<string>();
        var failedWorkers = new HashSet<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TotalTimeout);

        _signals?.Raise($"wave.orchestrator.started");

        try
        {
            var waveNumber = 0;
            var maxWave = _workers.Max(w => w.Wave);

            while (waveNumber <= maxWave && !cts.Token.IsCancellationRequested)
            {
                var waveWorkers = _workers
                    .Where(w => w.Wave == waveNumber)
                    .Where(w => !completedWorkers.Contains(w.Name))
                    .Where(w => IsCircuitClosed(w.Name))
                    .ToArray();

                if (waveWorkers.Length == 0)
                {
                    waveNumber++;
                    continue;
                }

                _signals?.Raise($"wave.started:wave={waveNumber}:workers={waveWorkers.Length}");

                var waveResults = await ExecuteWaveAsync(
                    waveWorkers,
                    input,
                    completedWorkers,
                    failedWorkers,
                    waveNumber,
                    cts.Token);

                results.AddRange(waveResults);

                // Check early exit condition
                if (_options.EarlyExitCondition != null)
                {
                    var lastResult = results.LastOrDefault();
                    if (lastResult != null && _options.EarlyExitCondition(lastResult))
                    {
                        _signals?.Raise($"wave.early_exit:wave={waveNumber}");
                        break;
                    }
                }

                waveNumber++;

                // Small delay between waves
                if (waveNumber <= maxWave && _options.WaveInterval > TimeSpan.Zero)
                {
                    await Task.Delay(_options.WaveInterval, cts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _signals?.Raise($"wave.timeout:elapsed={stopwatch.ElapsedMilliseconds}ms");
        }

        stopwatch.Stop();
        _signals?.Raise($"wave.orchestrator.completed:duration={stopwatch.ElapsedMilliseconds}ms:results={results.Count}");

        return new WaveOrchestratorResult<TOutput>
        {
            Results = results,
            CompletedWorkers = completedWorkers.ToList(),
            FailedWorkers = failedWorkers.ToList(),
            TotalDurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<List<TOutput>> ExecuteWaveAsync(
        WaveWorker<TInput, TOutput>[] workers,
        TInput input,
        HashSet<string> completedWorkers,
        HashSet<string> failedWorkers,
        int waveNumber,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<TOutput>();
        var maxParallel = _options.MaxParallelPerWave;

        // Check for per-wave parallelism override
        if (_options.ParallelismPerWave.TryGetValue(waveNumber, out var overrideValue))
        {
            maxParallel = overrideValue;
        }

        if (maxParallel <= 1 || workers.Length == 1)
        {
            // Sequential execution
            foreach (var worker in workers)
            {
                var result = await ExecuteWorkerAsync(
                    worker, input, completedWorkers, failedWorkers, cancellationToken);

                if (result != null)
                    results.Add(result);
            }
        }
        else
        {
            // Parallel execution with semaphore
            using var semaphore = new SemaphoreSlim(maxParallel);
            var tasks = workers.Select(async worker =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var result = await ExecuteWorkerAsync(
                        worker, input, completedWorkers, failedWorkers, cancellationToken);

                    if (result != null)
                        results.Add(result);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        return results.ToList();
    }

    private async Task<TOutput?> ExecuteWorkerAsync(
        WaveWorker<TInput, TOutput> worker,
        TInput input,
        HashSet<string> completedWorkers,
        HashSet<string> failedWorkers,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _signals?.Raise($"worker.started:{worker.Name}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.WorkerTimeout);

            var result = await worker.ProcessFunc(input, cts.Token);

            stopwatch.Stop();
            completedWorkers.Add(worker.Name);
            RecordSuccess(worker.Name);

            _signals?.Raise($"worker.completed:{worker.Name}:duration={stopwatch.ElapsedMilliseconds}ms");

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            failedWorkers.Add(worker.Name);
            RecordFailure(worker.Name);

            _signals?.Raise($"worker.timeout:{worker.Name}:duration={stopwatch.ElapsedMilliseconds}ms");

            return default;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            failedWorkers.Add(worker.Name);
            RecordFailure(worker.Name);

            _signals?.Raise($"worker.failed:{worker.Name}:duration={stopwatch.ElapsedMilliseconds}ms:error={ex.Message}");

            return default;
        }
    }

    #region Circuit Breaker

    private bool IsCircuitClosed(string workerName)
    {
        if (!_circuitBreakers.TryGetValue(workerName, out var state))
            return true;

        if (state.State == CircuitState.Closed)
            return true;

        if (state.State == CircuitState.Open)
        {
            // Check if enough time has passed to try again
            if (DateTimeOffset.UtcNow - state.LastFailure > _options.CircuitBreakerResetTime)
            {
                state.State = CircuitState.HalfOpen;
                return true;
            }
            return false;
        }

        // Half-open: allow one attempt
        return true;
    }

    private void RecordSuccess(string workerName)
    {
        if (_circuitBreakers.TryGetValue(workerName, out var state))
        {
            state.FailureCount = 0;
            state.State = CircuitState.Closed;
        }
    }

    private void RecordFailure(string workerName)
    {
        var state = _circuitBreakers.GetOrAdd(workerName, _ => new CircuitBreakerState());

        state.FailureCount++;
        state.LastFailure = DateTimeOffset.UtcNow;

        if (state.FailureCount >= _options.CircuitBreakerThreshold)
        {
            state.State = CircuitState.Open;
            _signals?.Raise($"circuit.opened:{workerName}:failures={state.FailureCount}");
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
///     Configuration for wave worker.
/// </summary>
public record WaveWorker<TInput, TOutput>(
    string Name,
    int Wave,
    int Priority,
    Func<TInput, CancellationToken, Task<TOutput>> ProcessFunc);

/// <summary>
///     Configuration options for WaveOrchestratorAtom.
/// </summary>
public class WaveOrchestratorOptions<TOutput>
{
    /// <summary>Total timeout for entire orchestration. Default: 5 seconds</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout per worker. Default: 2 seconds</summary>
    public TimeSpan WorkerTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay between waves. Default: 50ms</summary>
    public TimeSpan WaveInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Max parallel workers per wave (global default). Default: 4</summary>
    public int MaxParallelPerWave { get; set; } = 4;

    /// <summary>Per-wave parallelism overrides. Key=wave number, Value=max parallel for that wave</summary>
    public Dictionary<int, int> ParallelismPerWave { get; set; } = new();

    /// <summary>Circuit breaker: failures before opening. Default: 3</summary>
    public int CircuitBreakerThreshold { get; set; } = 3;

    /// <summary>Circuit breaker: time before retry after open. Default: 30 seconds</summary>
    public TimeSpan CircuitBreakerResetTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Early exit condition - if true, stop processing remaining waves</summary>
    public Func<TOutput, bool>? EarlyExitCondition { get; set; }
}

/// <summary>
///     Result from wave orchestration.
/// </summary>
public record WaveOrchestratorResult<TOutput>
{
    public List<TOutput> Results { get; init; } = new();
    public List<string> CompletedWorkers { get; init; } = new();
    public List<string> FailedWorkers { get; init; } = new();
    public double TotalDurationMs { get; init; }
}

/// <summary>
///     Circuit breaker state for a worker.
/// </summary>
internal class CircuitBreakerState
{
    public CircuitState State { get; set; } = CircuitState.Closed;
    public int FailureCount { get; set; }
    public DateTimeOffset LastFailure { get; set; }
}

internal enum CircuitState
{
    Closed,   // Normal operation
    Open,     // Failing, reject requests
    HalfOpen  // Trying one request to see if recovered
}
