namespace Mostlylucid.Ephemeral.Atoms.Retry;

/// <summary>
///     Wraps work with retry/backoff semantics using EphemeralWorkCoordinator under the hood.
/// </summary>
public sealed class RetryAtom<T> : IAsyncDisposable
{
    private readonly Func<int, TimeSpan> _backoff;
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly int _maxAttempts;

    public RetryAtom(
        Func<T, CancellationToken, Task> body,
        int maxAttempts = 3,
        Func<int, TimeSpan>? backoff = null,
        int? maxConcurrency = null,
        SignalSink? signals = null)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _maxAttempts = maxAttempts;
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(50 * attempt));

        _coordinator = new EphemeralWorkCoordinator<T>(
            async (item, ct) =>
            {
                var attempt = 0;
                while (true)
                    try
                    {
                        await body(item, ct).ConfigureAwait(false);
                        return;
                    }
                    catch when (++attempt < _maxAttempts && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(_backoff(attempt), ct).ConfigureAwait(false);
                    }
            },
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
                Signals = signals
            });
    }

    public ValueTask DisposeAsync()
    {
        return _coordinator.DisposeAsync();
    }

    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        return _coordinator.EnqueueWithIdAsync(item, ct);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }
}