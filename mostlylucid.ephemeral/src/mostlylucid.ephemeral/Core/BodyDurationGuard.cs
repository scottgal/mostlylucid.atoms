namespace Mostlylucid.Ephemeral;

/// <summary>
///     Thrown when an operation body exceeds its coordinator's configured max duration.
///     The coordinator stops waiting and frees its concurrency slot; it does not force-terminate
///     or cancel the body — that would require a per-item linked CancellationTokenSource, and
///     this bound is deliberately zero-allocation on the hot path. A non-cooperative body (and
///     even a cooperative one, since we never signal it) keeps running orphaned in the
///     background until it finishes or the coordinator itself shuts down. A hung body never
///     throws, so retry never triggers, which is why this bound is load-bearing and everything
///     built on top of it is ergonomics.
/// </summary>
public sealed class BodyDurationExceededException : TimeoutException
{
    public BodyDurationExceededException(TimeSpan maxDuration)
        : base($"Operation body exceeded its maximum allowed duration of {maxDuration}.")
    {
        MaxDuration = maxDuration;
    }

    public TimeSpan MaxDuration { get; }
}

/// <summary>
///     Shared bounded-execution helper used by every coordinator so the per-item duration
///     bound has one implementation instead of three drifting copies. Deliberately allocation-free
///     on the success path: no per-item CancellationTokenSource, no closure — <paramref name="item"/>
///     and <paramref name="body"/> are passed straight through to the existing stored delegate.
/// </summary>
internal static class BodyDurationGuard
{
    /// <summary>
    ///     Signal raised on the operation when its body trips the duration bound. Tripping must
    ///     be loud: a bound that quietly cancels and moves on turns a visible hang into an
    ///     invisible one.
    /// </summary>
    public const string TimeoutSignal = "coordinator.body.timeout";

    public static async Task RunBoundedAsync<T>(
        T item,
        Func<T, CancellationToken, Task> body,
        TimeSpan maxDuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var task = body(item, cancellationToken);
        try
        {
            await task.WaitAsync(maxDuration, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Observe(task);
            throw new BodyDurationExceededException(maxDuration);
        }
    }

    public static async Task<TResult> RunBoundedAsync<T, TResult>(
        T item,
        Func<T, CancellationToken, Task<TResult>> body,
        TimeSpan maxDuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var task = body(item, cancellationToken);
        try
        {
            return await task.WaitAsync(maxDuration, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Observe(task);
            throw new BodyDurationExceededException(maxDuration);
        }
    }

    /// <summary>
    ///     Prevents an unobserved-task-exception once we've stopped awaiting an orphaned body.
    /// </summary>
    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
