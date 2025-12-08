namespace Mostlylucid.Ephemeral.Patterns.DynamicConcurrency;

/// <summary>
///     Dynamic concurrency adjustment based on load signals.
/// </summary>
public sealed class DynamicConcurrencyDemo<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly int _max;
    private readonly int _min;
    private readonly string _scaleDownPattern;
    private readonly string _scaleUpPattern;

    public DynamicConcurrencyDemo(
        Func<T, CancellationToken, Task> body,
        SignalSink sink,
        int minConcurrency = 1,
        int maxConcurrency = 32,
        string scaleUpPattern = "load.high",
        string scaleDownPattern = "load.low")
    {
        _min = Math.Max(1, minConcurrency);
        _max = Math.Max(_min, maxConcurrency);
        _scaleUpPattern = scaleUpPattern;
        _scaleDownPattern = scaleDownPattern;

        _coordinator = new EphemeralWorkCoordinator<T>(
            body,
            new EphemeralOptions
            {
                MaxConcurrency = _min,
                EnableDynamicConcurrency = true,
                Signals = sink
            });

        _loop = Task.Run(() => WatchAsync(sink));
    }

    public int CurrentMaxConcurrency => _coordinator.CurrentMaxConcurrency;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
        }

        _cts.Dispose();
        await _coordinator.DisposeAsync();
    }

    private async Task WatchAsync(SignalSink sink)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var snapshot = sink.Sense();
                if (snapshot.Any(s => StringPatternMatcher.Matches(s.Signal, _scaleUpPattern)))
                {
                    var next = Math.Min(_max, _coordinator.CurrentMaxConcurrency * 2);
                    _coordinator.SetMaxConcurrency(next);
                }
                else if (snapshot.Any(s => StringPatternMatcher.Matches(s.Signal, _scaleDownPattern)))
                {
                    var next = Math.Max(_min, _coordinator.CurrentMaxConcurrency / 2);
                    _coordinator.SetMaxConcurrency(next);
                }
            }
            catch
            {
            }

            try
            {
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
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