using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Static extension methods for one-shot ephemeral parallel processing.
/// For long-lived coordinators, use EphemeralWorkCoordinator instead.
/// </summary>
public static class ParallelEphemeral
{
    /// <summary>
    /// Ephemeral parallel foreach:
    /// - Bounded concurrency
    /// - Keeps a small rolling window of recent operations
    /// - No payloads stored, only metadata
    /// </summary>
    public static async Task EphemeralForEachAsync<T>(
        this IEnumerable<T> source,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EphemeralOptions();

        using var concurrency = new SemaphoreSlim(options.MaxConcurrency);
        var recent = new ConcurrentQueue<EphemeralOperation>();
        var running = new ConcurrentBag<Task>();

        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

            var op = new EphemeralOperation(options.Signals, options.OnSignal, options.OnSignalRetracted, options.SignalConstraints);
            EnqueueEphemeral(op, recent, options);

            var task = ExecuteAsync(item, body, op, recent, options, cancellationToken, concurrency);
            running.Add(task);
        }

        // Wait for all in-flight work to complete
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>
    /// Keyed version:
    /// - Overall concurrency bounded by MaxConcurrency
    /// - Per-key concurrency bounded by MaxConcurrencyPerKey (default 1 = sequential pipelines per key)
    /// </summary>
    public static async Task EphemeralForEachAsync<T, TKey>(
        this IEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        options ??= new EphemeralOptions();

        using var globalConcurrency = new SemaphoreSlim(options.MaxConcurrency);
        var perKeyLocks = new ConcurrentDictionary<TKey, SemaphoreSlim>();
        var recent = new ConcurrentQueue<EphemeralOperation>();
        var running = new ConcurrentBag<Task>();

        try
        {
            foreach (var item in source)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = keySelector(item);
                var keyGate = perKeyLocks.GetOrAdd(
                    key,
                    _ => new SemaphoreSlim(options.MaxConcurrencyPerKey));

                await globalConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                await keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);

                var op = new EphemeralOperation(options.Signals, options.OnSignal, options.OnSignalRetracted, options.SignalConstraints) { Key = key?.ToString() };
                EnqueueEphemeral(op, recent, options);

                var task = ExecuteAsync(item, body, op, recent, options, cancellationToken, keyGate, globalConcurrency);
                running.Add(task);
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }
        finally
        {
            // Cleanup per-key gates - always dispose even on exception
            foreach (var gate in perKeyLocks.Values)
            {
                gate.Dispose();
            }
        }
    }

    private static async Task ExecuteAsync<T>(
        T item,
        Func<T, CancellationToken, Task> body,
        EphemeralOperation op,
        ConcurrentQueue<EphemeralOperation> recent,
        EphemeralOptions options,
        CancellationToken cancellationToken,
        params SemaphoreSlim[] semaphores)
    {
        try
        {
            await body(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            op.Error = ex;
        }
        finally
        {
            op.Completed = DateTimeOffset.UtcNow;
            foreach (var semaphore in semaphores)
                semaphore.Release();
            CleanupWindow(recent, options);
            SampleIfRequested(recent, options);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnqueueEphemeral(
        EphemeralOperation op,
        ConcurrentQueue<EphemeralOperation> recent,
        EphemeralOptions options)
    {
        recent.Enqueue(op);
        CleanupWindow(recent, options);
    }

    private static void CleanupWindow(
        ConcurrentQueue<EphemeralOperation> recent,
        EphemeralOptions options)
    {
        // Size-based eviction
        while (recent.Count > options.MaxTrackedOperations &&
               recent.TryDequeue(out _))
        {
        }

        // Age-based eviction (best-effort, don't overthink it)
        if (options.MaxOperationLifetime is { } maxAge &&
            recent.TryPeek(out var head))
        {
            var cutoff = DateTimeOffset.UtcNow - maxAge;

            while (head is not null && head.Started < cutoff &&
                   recent.TryDequeue(out _))
            {
                if (!recent.TryPeek(out head))
                    break;
            }
        }
    }

    private static void SampleIfRequested(
        ConcurrentQueue<EphemeralOperation> recent,
        EphemeralOptions options)
    {
        var sampler = options.OnSample;
        if (sampler is null) return;

        // Cheap snapshot; caller decides what to do
        var snapshot = recent
            .Select(x => x.ToSnapshot())
            .ToArray();

        if (snapshot.Length > 0)
        {
            sampler(snapshot);
        }
    }
}
