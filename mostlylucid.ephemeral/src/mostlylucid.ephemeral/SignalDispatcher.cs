using System.Threading;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Async dispatcher that fans signals into Ephemeral coordinators using pattern-based routing.
/// Emits stay synchronous; dispatch happens via internal coordinators.
/// </summary>
public sealed class SignalDispatcher : IAsyncDisposable
{
    private readonly object _rulesLock = new();
    private DispatchRule[] _rules = Array.Empty<DispatchRule>();
    private readonly EphemeralKeyedWorkCoordinator<SignalEvent, string> _coordinator;
    private readonly CancellationTokenSource _cts = new();

    private sealed record DispatchRule(string Pattern, Func<SignalEvent, Task> Handler);

    public SignalDispatcher(EphemeralOptions? coordinatorOptions = null)
    {
        // Keyed by signal name; per-signal sequential unless overridden via options.MaxConcurrencyPerKey
        _coordinator = new EphemeralKeyedWorkCoordinator<SignalEvent, string>(
            evt => evt.Signal,
            async (evt, ct) =>
            {
                var rules = Volatile.Read(ref _rules);
                foreach (var rule in rules)
                {
                    if (StringPatternMatcher.Matches(evt.Signal, rule.Pattern))
                    {
                        await rule.Handler(evt);
                    }
                }
            },
            coordinatorOptions ?? new EphemeralOptions { MaxConcurrency = Environment.ProcessorCount, MaxConcurrencyPerKey = 1, MaxTrackedOperations = 128, EnableDynamicConcurrency = false });
    }

    /// <summary>
    /// Register or replace a handler for a signal pattern (supports '*' and '?').
    /// Handlers are invoked in registration order when multiple patterns match.
    /// </summary>
    public void Register(string pattern, Func<SignalEvent, Task> handler)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentNullException(nameof(pattern));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_rulesLock)
        {
            // Replace existing pattern if present to keep order deterministic
            var existing = _rules;
            var replaced = false;
            var next = new DispatchRule[existing.Length + 1];

            for (var i = 0; i < existing.Length; i++)
            {
                if (!replaced && existing[i].Pattern == pattern)
                {
                    next[i] = new DispatchRule(pattern, handler);
                    replaced = true;
                }
                else
                {
                    next[i] = existing[i];
                }
            }

            if (!replaced)
            {
                next[existing.Length] = new DispatchRule(pattern, handler);
            }

            if (replaced)
            {
                // Shrink back to original length when we replaced
                next = next[..existing.Length];
            }

            Volatile.Write(ref _rules, next);
        }
    }

    /// <summary>
    /// Enqueue a signal for async dispatch. Returns false if dispatcher is cancelled.
    /// </summary>
    public bool Dispatch(SignalEvent evt)
    {
        if (_cts.IsCancellationRequested) return false;
        _ = _coordinator.EnqueueAsync(evt, _cts.Token);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _coordinator.DisposeAsync();
        _cts.Dispose();
    }
}
