namespace Mostlylucid.Helpers.Ephemeral.Examples;

/// <summary>
/// Demonstrates controlled fan-out: a global gate bounds total fan-out while per-key ordering is preserved.
/// Useful to prevent runaway task spawning when many keys spike simultaneously.
/// </summary>
public sealed class ControlledFanOut<TKey, T> : IAsyncDisposable where TKey : notnull
{
    private readonly EphemeralKeyedWorkCoordinator<T, TKey> _coordinator;

    public ControlledFanOut(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        int maxGlobalConcurrency,
        int perKeyConcurrency = 1,
        SignalSink? sink = null)
    {
        _coordinator = new EphemeralKeyedWorkCoordinator<T, TKey>(
            keySelector,
            body,
            new EphemeralOptions
            {
                MaxConcurrency = maxGlobalConcurrency,
                MaxConcurrencyPerKey = Math.Max(1, perKeyConcurrency),
                Signals = sink
            });
    }

    public ValueTask EnqueueAsync(T item, CancellationToken ct = default) =>
        _coordinator.EnqueueAsync(item, ct);

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();
}
