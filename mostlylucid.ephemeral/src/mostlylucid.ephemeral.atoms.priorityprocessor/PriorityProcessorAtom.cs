using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mostlylucid.Ephemeral.Atoms.PriorityProcessor;

/// <summary>
/// Processor atom that listens to signals with priority semantics.
/// When a processor fails repeatedly, it emits failover signals for lower priority atoms.
/// Part of the Dynamic Adaptive Workflow pattern for priority-based failover.
/// </summary>
/// <remarks>
/// <para><strong>Pattern:</strong> Priority-Based Failover with Health Monitoring</para>
/// <para><strong>Use Cases:</strong></para>
/// <list type="bullet">
/// <item>Multi-processor workflows with automatic failover</item>
/// <item>Primary/backup processor architectures</item>
/// <item>Resilient processing pipelines with redundancy</item>
/// </list>
/// <para><strong>Signals Emitted:</strong></para>
/// <list type="bullet">
/// <item><c>processing.started:pri{N}:{entityKey}</c> - Processing began</item>
/// <item><c>processing.complete:pri{N}:{entityKey}</c> - Success</item>
/// <item><c>processing.failed:pri{N}:{entityKey}</c> - Failure</item>
/// <item><c>processor.{atom}.unhealthy</c> - Health threshold exceeded</item>
/// <item><c>failover.requested:pri{N}→pri{N+1}</c> - Request routing change</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var sink = new SignalSink();
/// var context = new SignalContext("workflow", "orders", "ProcessorA");
///
/// var processor = new PriorityProcessorAtom(
///     sink,
///     context,
///     priority: 1,
///     listenSignal: "order.placed",
///     processFunc: async (entityKey, ct) =>
///     {
///         // Your processing logic
///         return await ProcessOrderAsync(entityKey, ct);
///     });
/// </code>
/// </example>
public sealed class PriorityProcessorAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly SignalContext _context;
    private readonly int _priority;
    private readonly string _listenSignal;
    private readonly Func<string, CancellationToken, Task<bool>> _processFunc;
    private readonly IDisposable _subscription;
    private bool _isHealthy = true;
    private int _consecutiveFailures = 0;
    private readonly int _failureThreshold;

    /// <summary>
    /// Creates a priority processor atom that handles work at a specific priority level.
    /// </summary>
    /// <param name="signals">Shared signal sink for coordination</param>
    /// <param name="context">Signal context (Sink.Coordinator.Atom hierarchy)</param>
    /// <param name="priority">Priority level (1 = highest, 2 = backup, etc.)</param>
    /// <param name="listenSignal">Signal pattern to listen for (e.g., "order.placed")</param>
    /// <param name="processFunc">Processing function returning true on success, false on failure</param>
    /// <param name="failureThreshold">Consecutive failures before marking unhealthy (default: 3)</param>
    public PriorityProcessorAtom(
        SignalSink signals,
        SignalContext context,
        int priority,
        string listenSignal,
        Func<string, CancellationToken, Task<bool>> processFunc,
        int failureThreshold = 3)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _context = context;
        _priority = priority;
        _listenSignal = listenSignal ?? throw new ArgumentNullException(nameof(listenSignal));
        _processFunc = processFunc ?? throw new ArgumentNullException(nameof(processFunc));
        _failureThreshold = failureThreshold;

        _subscription = _signals.Subscribe(OnSignal);
    }

    /// <summary>
    /// Current health status of this processor.
    /// </summary>
    public bool IsHealthy => _isHealthy;

    /// <summary>
    /// Current count of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Priority level of this processor.
    /// </summary>
    public int Priority => _priority;

    private async void OnSignal(SignalEvent signal)
    {
        // Only process if signal matches pattern and we're assigned via routing
        if (!signal.Signal.StartsWith(_listenSignal))
            return;

        // Extract entity key (e.g., "order.placed:WIDGET-123" → "WIDGET-123")
        var entityKey = signal.Key ?? signal.Signal.Split(':').LastOrDefault();
        if (string.IsNullOrEmpty(entityKey))
            return;

        // Check if we should process based on current routing rules
        var routingSignal = $"route.priority.{_priority}:{entityKey}";
        var shouldProcess = _signals.Sense(evt => evt.Signal == routingSignal).Any();

        if (!shouldProcess && _priority != 1) // Priority 1 always tries first
            return;

        var emitter = new ScopedSignalEmitter(_context, signal.OperationId, _signals);

        try
        {
            emitter.Emit($"processing.started:pri{_priority}:{entityKey}");

            var success = await _processFunc(entityKey, CancellationToken.None);

            if (success)
            {
                _consecutiveFailures = 0;
                _isHealthy = true;
                emitter.Emit($"processing.complete:pri{_priority}:{entityKey}");

                // Signal success for health monitoring
                _signals.Raise(new SignalEvent(
                    $"processor.{_context.Atom}.health.good",
                    signal.OperationId,
                    entityKey,
                    DateTimeOffset.UtcNow));
            }
            else
            {
                HandleFailure(emitter, entityKey, signal.OperationId);
            }
        }
        catch (Exception ex)
        {
            HandleFailure(emitter, entityKey, signal.OperationId, ex);
        }
    }

    private void HandleFailure(ScopedSignalEmitter emitter, string entityKey, long operationId, Exception? ex = null)
    {
        _consecutiveFailures++;
        emitter.Emit($"processing.failed:pri{_priority}:{entityKey}");

        if (ex != null)
        {
            emitter.Emit($"processing.exception:pri{_priority}:{ex.GetType().Name}");
        }

        if (_consecutiveFailures >= _failureThreshold && _isHealthy)
        {
            _isHealthy = false;

            // Emit failover signal for router
            _signals.Raise(new SignalEvent(
                $"processor.{_context.Atom}.unhealthy",
                operationId,
                $"pri{_priority}:failures={_consecutiveFailures}",
                DateTimeOffset.UtcNow));

            // Trigger failover to next priority
            _signals.Raise(new SignalEvent(
                $"failover.requested:pri{_priority}→pri{_priority + 1}",
                operationId,
                entityKey,
                DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Manually reset health status and failure count.
    /// </summary>
    public void ResetHealth()
    {
        _isHealthy = true;
        _consecutiveFailures = 0;
    }

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
    }
}
