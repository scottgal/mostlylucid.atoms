using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;

namespace Mostlylucid.Ephemeral.Sqlite;

/// <summary>
///     Demonstrates ephemeral patterns using SQLite as the example domain:
///     - Single-writer coordination via EphemeralWorkCoordinator (MaxConcurrency=1)
///     - Signal-based sampling for write observability
///     - Cached reads with signal-driven invalidation
///     - Connection-string keyed instances (per-database coordination)
/// </summary>
public sealed class SqliteSingleWriter : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SqliteSingleWriter> Instances = new();
    private readonly EphemeralLruCache<string, object?> _cache;

    private readonly string _connectionString;
    private readonly Action<string> _emitSignal;
    private readonly string _instanceId;
    private readonly SqliteSingleWriterOptions _options;
    private readonly int _sampleRate;
    private readonly SignalSink _signals;
    private readonly EphemeralWorkCoordinator<WriteCommand> _writeCoordinator;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;
    private CancellationTokenSource? _externalInvalidationCts;
    private Task? _externalInvalidationTask;
    private SqliteConnection? _writeConnection;
    private int _writeCount;
    private bool _writePragmasApplied;

    private SqliteSingleWriter(string connectionString, SqliteSingleWriterOptions? options = null)
    {
        _connectionString = connectionString;
        _options = options ?? new SqliteSingleWriterOptions();
        _sampleRate = Math.Max(1, _options.SampleRate);
        _instanceId = $"sqlite:{EphemeralIdGenerator.NextId()}";
        _signals = new SignalSink(
            _options.MaxTrackedWrites * 2,
            TimeSpan.FromMinutes(5));
        _emitSignal = name =>
            _signals.Raise(new SignalEvent(name, EphemeralIdGenerator.NextId(), _instanceId, DateTimeOffset.UtcNow));

        _cache = new EphemeralLruCache<string, object?>(
            new EphemeralLruCacheOptions
            {
                DefaultTtl = _options.DefaultCacheDuration,
                HotKeyExtension = _options.HotKeyExtension,
                HotAccessThreshold = _options.HotAccessThreshold,
                MaxSize = (int)_options.CacheSizeLimit,
                SampleRate = _options.SampleRate
            });

        // Single-writer pattern: MaxConcurrency=1 ensures serialized writes
        _writeCoordinator = new EphemeralWorkCoordinator<WriteCommand>(
            async (cmd, ct) => await ExecuteWriteInternalAsync(cmd, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = _options.MaxTrackedWrites,
                Signals = _signals
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Instances.TryRemove(_connectionString, out _);

        _writeCoordinator.Complete();
        await _writeCoordinator.DrainAsync().ConfigureAwait(false);
        await _writeCoordinator.DisposeAsync().ConfigureAwait(false);

        if (_externalInvalidationCts != null)
        {
            _externalInvalidationCts.Cancel();
            if (_externalInvalidationTask != null)
                try
                {
                    await _externalInvalidationTask.ConfigureAwait(false);
                }
                catch
                {
                }

            _externalInvalidationCts.Dispose();
        }

        if (_writeConnection != null)
            await _writeConnection.DisposeAsync().ConfigureAwait(false);

        _writeLock.Dispose();
        await _cache.DisposeAsync();
    }

    /// <summary>
    ///     Gets or creates a SqliteSingleWriter instance for the specified connection string.
    /// </summary>
    public static SqliteSingleWriter GetOrCreate(string connectionString, SqliteSingleWriterOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        return Instances.GetOrAdd(connectionString, cs => new SqliteSingleWriter(cs, options));
    }

    /// <summary>
    ///     Executes a write command with serialized access. Samples write signals.
    /// </summary>
    public async Task<int> WriteAsync(string sql, object? parameters = null, CancellationToken ct = default)
    {
        var command = WriteCommand.ForSql(sql, parameters, ShouldSample(), _options.DefaultCommandTimeoutSeconds,
            _instanceId, _emitSignal);
        _signals.Raise(new SignalEvent("write.enqueue", command.Completion.Task.Id, _instanceId,
            DateTimeOffset.UtcNow));
        await _writeCoordinator.EnqueueAsync(command, ct);
        var result = await command.Completion.Task.WaitAsync(ct);
        return result.RowsAffected;
    }

    /// <summary>
    ///     Executes multiple statements as a single logical write (optionally transactional).
    /// </summary>
    public async Task<int> WriteBatchAsync(IEnumerable<(string Sql, object? Parameters)> commands,
        bool transactional = true,
        CancellationToken ct = default)
    {
        var commandList = commands?.ToList() ?? throw new ArgumentNullException(nameof(commands));
        if (commandList.Count == 0)
            return 0;

        var writeCommand = WriteCommand.ForBatch(commandList, transactional, ShouldSample(),
            _options.DefaultCommandTimeoutSeconds, _instanceId, _emitSignal);
        _signals.Raise(new SignalEvent("write.batch.enqueue", writeCommand.Completion.Task.Id, _instanceId,
            DateTimeOffset.UtcNow));
        await _writeCoordinator.EnqueueAsync(writeCommand, ct);
        var result = await writeCommand.Completion.Task.WaitAsync(ct);
        return result.RowsAffected;
    }

    /// <summary>
    ///     Runs user-provided work inside the single-writer connection and a transaction.
    ///     Use for complex updates that span multiple statements.
    /// </summary>
    public async Task ExecuteInTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work,
        CancellationToken ct = default)
    {
        await ExecuteInTransactionAsync<object?>(async (conn, tx, innerCt) =>
        {
            await work(conn, tx, innerCt).ConfigureAwait(false);
            return null;
        }, ct);
    }

    /// <summary>
    ///     Runs user-provided work inside the single-writer connection and a transaction, returning a value.
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        var command = WriteCommand.ForTransactional(work, ShouldSample(), _instanceId, _emitSignal);
        _signals.Raise(new SignalEvent("write.tx.enqueue", command.Completion.Task.Id, _instanceId,
            DateTimeOffset.UtcNow));
        await _writeCoordinator.EnqueueAsync(command, ct);
        var result = await command.Completion.Task.WaitAsync(ct);
        return result.Result is T typed ? typed : default!;
    }

    /// <summary>
    ///     Executes a write and invalidates related cache keys via signals.
    /// </summary>
    public async Task<int> WriteAndInvalidateAsync(string sql, object? parameters = null,
        IEnumerable<string>? cacheKeysToInvalidate = null, CancellationToken ct = default)
    {
        var result = await WriteAsync(sql, parameters, ct);

        if (cacheKeysToInvalidate != null)
            foreach (var key in cacheKeysToInvalidate)
            {
                _cache.Invalidate(key);
                _signals.Raise(new SignalEvent($"cache.invalidate:{key}", EphemeralIdGenerator.NextId(), null,
                    DateTimeOffset.UtcNow));
            }

        return result;
    }

    /// <summary>
    ///     Reads data with caching. Emits cache hit/miss signals for observability.
    /// </summary>
    public async Task<T?> ReadAsync<T>(string cacheKey, Func<SqliteConnection, Task<T>> reader,
        TimeSpan? slidingExpiration = null, CancellationToken ct = default)
    {
        if (_cache.TryGet(cacheKey, out var cachedObj))
        {
            if (ShouldSample())
                _signals.Raise(new SignalEvent($"cache.hit:{cacheKey}", EphemeralIdGenerator.NextId(), _instanceId,
                    DateTimeOffset.UtcNow));
            return cachedObj is T cached ? cached : default;
        }

        if (ShouldSample())
            _signals.Raise(new SignalEvent($"cache.miss:{cacheKey}", EphemeralIdGenerator.NextId(), _instanceId,
                DateTimeOffset.UtcNow));

        var result = await _cache.GetOrAddAsync(cacheKey, async _ =>
        {
            // Read connections can be concurrent - SQLite handles this
            await using var connection = await CreateReadConnectionAsync(ct);
            if (ShouldSample())
                _signals.Raise(new SignalEvent($"read.start:{cacheKey}", EphemeralIdGenerator.NextId(), _instanceId,
                    DateTimeOffset.UtcNow));

            var computed = await reader(connection);

            _signals.Raise(new SignalEvent($"cache.set:{cacheKey}", EphemeralIdGenerator.NextId(), _instanceId,
                DateTimeOffset.UtcNow));

            if (ShouldSample())
                _signals.Raise(new SignalEvent($"read.done:{cacheKey}", EphemeralIdGenerator.NextId(), _instanceId,
                    DateTimeOffset.UtcNow));

            return computed!;
        }, slidingExpiration ?? _options.DefaultCacheDuration);

        return result is T typed ? typed : default;
    }

    /// <summary>
    ///     Reads data without caching.
    /// </summary>
    public async Task<T> QueryAsync<T>(Func<SqliteConnection, Task<T>> reader, CancellationToken ct = default)
    {
        await using var connection = await CreateReadConnectionAsync(ct);
        if (ShouldSample())
            _signals.Raise(new SignalEvent("query.start", EphemeralIdGenerator.NextId(), _instanceId,
                DateTimeOffset.UtcNow));
        return await reader(connection);
    }

    /// <summary>
    ///     Gets recent signals (sampled write operations, cache hits/misses).
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return _signals.Sense();
    }

    /// <summary>
    ///     Gets signals matching a pattern (e.g., "write.*", "cache.*").
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(string pattern)
    {
        return _signals.Sense(s => StringPatternMatcher.Matches(s.Signal, pattern));
    }

    /// <summary>
    ///     Gets a snapshot of current write operations.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetWriteSnapshot()
    {
        return _writeCoordinator.GetSnapshot();
    }

    /// <summary>
    ///     Invalidates cache and emits signal.
    /// </summary>
    public void InvalidateCache(string cacheKey)
    {
        _cache.Invalidate(cacheKey);
        _signals.Raise(new SignalEvent($"cache.invalidate:{cacheKey}", EphemeralIdGenerator.NextId(), _instanceId,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Waits for all pending writes to complete.
    /// </summary>
    public async Task FlushWritesAsync(CancellationToken ct = default)
    {
        _signals.Raise(new SignalEvent("write.flush.start", EphemeralIdGenerator.NextId(), _instanceId,
            DateTimeOffset.UtcNow));
        var barrier = WriteCommand.Barrier();
        await _writeCoordinator.EnqueueAsync(barrier, ct);
        await barrier.Completion.Task.WaitAsync(ct);
        _signals.Raise(new SignalEvent("write.flush.done", EphemeralIdGenerator.NextId(), _instanceId,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Enables central cache invalidation driven by an external signal sink (defaults to "cache.invalidate:*").
    ///     Safe to call once; subsequent calls are ignored.
    /// </summary>
    public void EnableSignalDrivenInvalidation(SignalSink sink, IEnumerable<string>? patterns = null,
        TimeSpan? pollInterval = null)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (_externalInvalidationTask != null) return;

        var patternSet =
            new HashSet<string>(patterns ?? new[] { "cache.invalidate:*" }, StringComparer.OrdinalIgnoreCase);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);

        _externalInvalidationCts = new CancellationTokenSource();
        _externalInvalidationTask = Task.Run(() =>
            MonitorExternalInvalidationsAsync(sink, patternSet, interval, _externalInvalidationCts.Token));
    }

    private bool ShouldSample()
    {
        var count = Interlocked.Increment(ref _writeCount);
        return count % _sampleRate == 0;
    }

    private async Task<SqliteConnection> GetWriteConnectionAsync(CancellationToken ct)
    {
        _writeConnection ??= new SqliteConnection(_connectionString);

        if (_writeConnection.State != ConnectionState.Open)
            await _writeConnection.OpenAsync(ct).ConfigureAwait(false);
        _signals.Raise(new SignalEvent("connection.open.write", EphemeralIdGenerator.NextId(), _instanceId,
            DateTimeOffset.UtcNow));

        if (!_writePragmasApplied)
        {
            await ApplyPragmasAsync(_writeConnection, true, ct).ConfigureAwait(false);
            _writePragmasApplied = true;
        }

        return _writeConnection;
    }

    private async Task<SqliteConnection> CreateReadConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        _signals.Raise(new SignalEvent("connection.open.read", EphemeralIdGenerator.NextId(), _instanceId,
            DateTimeOffset.UtcNow));
        await ApplyPragmasAsync(connection, false, ct).ConfigureAwait(false);
        return connection;
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, bool isWriter, CancellationToken ct)
    {
        if (_options.BusyTimeout > TimeSpan.Zero)
        {
            await using var busyCmd = connection.CreateCommand();
            busyCmd.CommandText = $"PRAGMA busy_timeout={(int)_options.BusyTimeout.TotalMilliseconds};";
            await busyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _signals.Raise(new SignalEvent("pragma.busy_timeout", EphemeralIdGenerator.NextId(), _instanceId,
                DateTimeOffset.UtcNow));
        }

        if (_options.EnforceForeignKeys)
        {
            await using var fkCmd = connection.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await fkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _signals.Raise(new SignalEvent("pragma.foreign_keys.on", EphemeralIdGenerator.NextId(), _instanceId,
                DateTimeOffset.UtcNow));
        }

        if (isWriter && _options.EnableWriteAheadLogging)
        {
            await using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _signals.Raise(new SignalEvent("pragma.wal.on", EphemeralIdGenerator.NextId(), _instanceId,
                DateTimeOffset.UtcNow));
        }
    }

    private async Task ExecuteWriteInternalAsync(WriteCommand cmd, CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (cmd.EmitSignal)
            _signals.Raise(new SignalEvent($"{cmd.Operation}.start", EphemeralIdGenerator.NextId(), _instanceId,
                startTime));

        await _writeLock.WaitAsync(ct);
        try
        {
            var connection = await GetWriteConnectionAsync(ct).ConfigureAwait(false);
            var result = await cmd.Executor(connection, ct).ConfigureAwait(false);
            cmd.Completion.TrySetResult(result);

            if (cmd.EmitSignal)
            {
                var duration = DateTimeOffset.UtcNow - startTime;
                _signals.Raise(new SignalEvent(
                    $"{cmd.Operation}.done:{result.RowsAffected}rows:{duration.TotalMilliseconds:F0}ms",
                    EphemeralIdGenerator.NextId(), _instanceId, DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            cmd.Completion.TrySetException(ex);
            _signals.Raise(new SignalEvent($"{cmd.Operation}.error:{ex.Message[..Math.Min(50, ex.Message.Length)]}",
                EphemeralIdGenerator.NextId(), _instanceId, DateTimeOffset.UtcNow));
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task MonitorExternalInvalidationsAsync(
        SignalSink sink,
        HashSet<string> patterns,
        TimeSpan interval,
        CancellationToken ct)
    {
        var processed = new HashSet<long>();
        while (!ct.IsCancellationRequested)
            try
            {
                var signals = sink.Sense();
                foreach (var signal in signals)
                {
                    if (!processed.Add(signal.OperationId))
                        continue;

                    if (!patterns.Any(p => StringPatternMatcher.Matches(signal.Signal, p)))
                        continue;

                    var key = ExtractCacheKey(signal.Signal);
                    if (key is null) continue;

                    _cache.Invalidate(key);
                    _emitSignal($"cache.invalidate.external:{key}");
                }

                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch
            {
                // Swallow to keep loop alive
            }
    }

    private static string? ExtractCacheKey(string signal)
    {
        const string prefix = "cache.invalidate:";
        if (!signal.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        return signal.Length > prefix.Length ? signal[prefix.Length..] : null;
    }
}

/// <summary>
///     Configuration options for SqliteSingleWriter.
/// </summary>
public sealed class SqliteSingleWriterOptions
{
    /// <summary>
    ///     Maximum number of items in the cache. Default: 1000.
    /// </summary>
    public long CacheSizeLimit { get; set; } = 1000;

    /// <summary>
    ///     Default cache duration for read operations (base TTL). Default: 5 minutes.
    /// </summary>
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Extended TTL for hot keys. Default: 30 minutes.
    /// </summary>
    public TimeSpan HotKeyExtension { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Accesses before a key is considered hot. Default: 3.
    /// </summary>
    public int HotAccessThreshold { get; set; } = 3;

    /// <summary>
    ///     Maximum number of write operations to track. Default: 128.
    /// </summary>
    public int MaxTrackedWrites { get; set; } = 128;

    /// <summary>
    ///     Signal sampling rate. 1 = every operation, 10 = every 10th. Default: 1.
    /// </summary>
    public int SampleRate { get; set; } = 1;

    /// <summary>
    ///     PRAGMA busy_timeout applied to all connections. Default: 10 seconds.
    /// </summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Default command timeout for generated commands. Default: 30 seconds.
    /// </summary>
    public int DefaultCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Whether to enable WAL mode on the writer connection. Default: true.
    /// </summary>
    public bool EnableWriteAheadLogging { get; set; } = true;

    /// <summary>
    ///     Whether to enforce foreign keys on all connections. Default: true.
    /// </summary>
    public bool EnforceForeignKeys { get; set; } = true;
}