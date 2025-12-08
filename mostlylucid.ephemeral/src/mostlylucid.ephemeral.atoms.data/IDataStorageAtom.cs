namespace Mostlylucid.Ephemeral.Atoms.Data;

/// <summary>
///     Interface for data storage atoms that listen to signals for transparent persistence.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
/// <typeparam name="TValue">Type of the value.</typeparam>
public interface IDataStorageAtom<TKey, TValue> : IAsyncDisposable
    where TKey : notnull
{
    /// <summary>
    ///     Configuration for this storage atom.
    /// </summary>
    DataStorageConfig Config { get; }

    /// <summary>
    ///     Saves a value by key. Can be called directly or triggered via signal.
    /// </summary>
    Task SaveAsync(TKey key, TValue value, CancellationToken ct = default);

    /// <summary>
    ///     Loads a value by key. Returns default if not found.
    /// </summary>
    Task<TValue?> LoadAsync(TKey key, CancellationToken ct = default);

    /// <summary>
    ///     Deletes a value by key.
    /// </summary>
    Task DeleteAsync(TKey key, CancellationToken ct = default);

    /// <summary>
    ///     Checks if a key exists.
    /// </summary>
    Task<bool> ExistsAsync(TKey key, CancellationToken ct = default);
}

/// <summary>
///     Base class for data storage atoms with signal handling.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
/// <typeparam name="TValue">Type of the value.</typeparam>
public abstract class DataStorageAtomBase<TKey, TValue> : IDataStorageAtom<TKey, TValue>
    where TKey : notnull
{
    private readonly Action<SignalEvent> _signalHandler;
    protected readonly EphemeralWorkCoordinator<DataOperation<TKey, TValue>> Coordinator;
    protected readonly SignalSink Signals;

    protected DataStorageAtomBase(SignalSink signals, DataStorageConfig config)
    {
        Signals = signals ?? throw new ArgumentNullException(nameof(signals));
        Config = config ?? throw new ArgumentNullException(nameof(config));

        var options = new EphemeralOptions
        {
            MaxConcurrency = config.MaxConcurrency,
            Signals = signals
        };

        Coordinator = new EphemeralWorkCoordinator<DataOperation<TKey, TValue>>(
            ExecuteOperationAsync,
            options);

        _signalHandler = OnSignal;
        Signals.SignalRaised += _signalHandler;
    }

    public DataStorageConfig Config { get; }

    public async Task SaveAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<object?>();
        var op = new DataOperation<TKey, TValue>(DataOperationType.Save, key, value, tcs);
        await Coordinator.EnqueueAsync(op, ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<TValue?> LoadAsync(TKey key, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<object?>();
        var op = new DataOperation<TKey, TValue>(DataOperationType.Load, key, default, tcs);
        await Coordinator.EnqueueAsync(op, ct).ConfigureAwait(false);
        var result = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        return result is TValue val ? val : default;
    }

    public async Task DeleteAsync(TKey key, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<object?>();
        var op = new DataOperation<TKey, TValue>(DataOperationType.Delete, key, default, tcs);
        await Coordinator.EnqueueAsync(op, ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(TKey key, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<object?>();
        var op = new DataOperation<TKey, TValue>(DataOperationType.Exists, key, default, tcs);
        await Coordinator.EnqueueAsync(op, ct).ConfigureAwait(false);
        var result = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        return result is true;
    }

    public virtual async ValueTask DisposeAsync()
    {
        Signals.SignalRaised -= _signalHandler;
        Coordinator.Complete();
        await Coordinator.DrainAsync().ConfigureAwait(false);
        await Coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSignal(SignalEvent signal)
    {
        // Check for save signal
        if (StringPatternMatcher.Matches(signal.Signal, Config.SaveSignalPattern) ||
            StringPatternMatcher.Matches(signal.Signal, $"{Config.SaveSignalPattern}.*"))
            // Signal payload should contain the data
            return;

        // Check for delete signal
        if (StringPatternMatcher.Matches(signal.Signal, Config.DeleteSignalPattern) ||
            StringPatternMatcher.Matches(signal.Signal, $"{Config.DeleteSignalPattern}.*"))
        {
        }
    }

    private async Task ExecuteOperationAsync(DataOperation<TKey, TValue> op, CancellationToken ct)
    {
        try
        {
            switch (op.Type)
            {
                case DataOperationType.Save:
                    await SaveInternalAsync(op.Key, op.Value!, ct).ConfigureAwait(false);
                    if (Config.EmitCompletionSignals)
                        Signals.Raise($"saved.data.{Config.DatabaseName}", op.Key?.ToString());
                    op.Completion?.TrySetResult(true);
                    break;

                case DataOperationType.Load:
                    var value = await LoadInternalAsync(op.Key, ct).ConfigureAwait(false);
                    op.Completion?.TrySetResult(value);
                    break;

                case DataOperationType.Delete:
                    await DeleteInternalAsync(op.Key, ct).ConfigureAwait(false);
                    if (Config.EmitCompletionSignals)
                        Signals.Raise($"deleted.data.{Config.DatabaseName}", op.Key?.ToString());
                    op.Completion?.TrySetResult(true);
                    break;

                case DataOperationType.Exists:
                    var exists = await ExistsInternalAsync(op.Key, ct).ConfigureAwait(false);
                    op.Completion?.TrySetResult(exists);
                    break;
            }
        }
        catch (Exception ex)
        {
            Signals.Raise($"error.data.{Config.DatabaseName}:{ex.GetType().Name}", op.Key?.ToString());
            op.Completion?.TrySetException(ex);
        }
    }

    /// <summary>
    ///     Enqueue a save operation triggered by signal (fire-and-forget style).
    /// </summary>
    public void EnqueueSave(TKey key, TValue value)
    {
        var op = new DataOperation<TKey, TValue>(DataOperationType.Save, key, value, null);
        _ = Coordinator.EnqueueAsync(op, CancellationToken.None);
    }

    /// <summary>
    ///     Enqueue a delete operation triggered by signal (fire-and-forget style).
    /// </summary>
    public void EnqueueDelete(TKey key)
    {
        var op = new DataOperation<TKey, TValue>(DataOperationType.Delete, key, default, null);
        _ = Coordinator.EnqueueAsync(op, CancellationToken.None);
    }

    protected abstract Task SaveInternalAsync(TKey key, TValue value, CancellationToken ct);
    protected abstract Task<TValue?> LoadInternalAsync(TKey key, CancellationToken ct);
    protected abstract Task DeleteInternalAsync(TKey key, CancellationToken ct);
    protected abstract Task<bool> ExistsInternalAsync(TKey key, CancellationToken ct);
}

/// <summary>
///     Type of data operation.
/// </summary>
public enum DataOperationType
{
    Save,
    Load,
    Delete,
    Exists
}

/// <summary>
///     Internal data operation record.
/// </summary>
public record DataOperation<TKey, TValue>(
    DataOperationType Type,
    TKey Key,
    TValue? Value,
    TaskCompletionSource<object?>? Completion);