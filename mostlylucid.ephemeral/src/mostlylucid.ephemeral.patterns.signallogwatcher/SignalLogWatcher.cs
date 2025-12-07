using System.Collections.Concurrent;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.SignalLogWatcher;

/// <summary>
/// Watches a SignalSink for error-like signals (glob patterns) and triggers a callback.
/// Demonstrates a singleton coordinator that reacts to the moving window.
/// </summary>
public sealed class SignalLogWatcher : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly string _pattern;
    private readonly Action<SignalEvent> _onMatch;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentDictionary<(long Id, string Signal), byte> _seen = new();

    public SignalLogWatcher(
        SignalSink sink,
        Action<SignalEvent> onMatch,
        string pattern = "error.*",
        TimeSpan? pollInterval = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _onMatch = onMatch ?? throw new ArgumentNullException(nameof(onMatch));
        _pattern = pattern;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);

        _loop = Task.Run(WatchAsync);
    }

    private async Task WatchAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var snapshot = _sink.Sense();
                foreach (var evt in snapshot)
                {
                    var key = (evt.OperationId, evt.Signal);
                    if (_seen.ContainsKey(key))
                        continue;

                    if (StringPatternMatcher.Matches(evt.Signal, _pattern))
                    {
                        _onMatch(evt);
                        _seen[key] = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                // Emit error signal so callers can observe watcher health
                _sink.Raise(new SignalEvent(
                    $"watcher.error:{ex.GetType().Name}",
                    EphemeralIdGenerator.NextId(),
                    _pattern,
                    DateTimeOffset.UtcNow));
            }

            try
            {
                await Task.Delay(_pollInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
