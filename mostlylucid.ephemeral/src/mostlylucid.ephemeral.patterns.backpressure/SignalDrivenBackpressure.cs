using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.Backpressure;

/// <summary>
/// Signal-driven backpressure: when "backpressure.*" is present, the queue defers starts.
/// </summary>
public static class SignalDrivenBackpressure
{
    public static EphemeralWorkCoordinator<T> Create<T>(
        Func<T, CancellationToken, Task> body,
        SignalSink sink,
        int maxConcurrency = 4)
    {
        return new EphemeralWorkCoordinator<T>(
            body,
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency,
                Signals = sink,
                DeferOnSignals = new HashSet<string> { "backpressure.*" },
                DeferCheckInterval = TimeSpan.FromMilliseconds(50),
                MaxDeferAttempts = 200
            });
    }
}
