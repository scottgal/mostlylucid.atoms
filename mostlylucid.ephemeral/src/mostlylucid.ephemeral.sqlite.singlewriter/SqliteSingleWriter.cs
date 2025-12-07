using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;

namespace Mostlylucid.Ephemeral.Sqlite;

/// <summary>
/// Demonstrates ephemeral patterns using SQLite as the example domain:
/// - Single-writer coordination via EphemeralWorkCoordinator (MaxConcurrency=1)
/// - Signal-based sampling for write observability
/// - Cached reads with signal-driven invalidation
/// - Connection-string keyed instances (per-database coordination)
/// </summary>
public sealed class SqliteSingleWriter : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SqliteSingleWriter> Instances = new();

    private readonly string _connectionString;
    private readonly EphemeralWorkCoordinator<WriteCommand> _writeCoordinator;
    private readonly SignalSink _signals;
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _defaultCacheOptions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _sampleRate;
    private int _writeCount;
    private SqliteConnection? _writeConnection;
    private bool _disposed;

    /// <summary>
    /// Gets or creates a SqliteSingleWriter instance for the specified connection string.
    /// </summary>
    public static SqliteSingleWriter GetOrCreate(string connectionString, SqliteSingleWriterOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        return Instances.GetOrAdd(connectionString, cs => new SqliteSingleWriter(cs, options));
    }

    private SqliteSingleWriter(string connectionString, SqliteSingleWriterOptions? options = null)
    {
        _connectionString = connectionString;
        options ??= new SqliteSingleWriterOptions();
        _sampleRate = Math.Max(1, options.SampleRate);

        _signals = new SignalSink(
            maxCapacity: options.MaxTrackedWrites * 2,
            maxAge: TimeSpan.FromMinutes(5));

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = options.CacheSizeLimit
        });

        _defaultCacheOptions = new MemoryCacheEntryOptions
        {
            SlidingExpiration = options.DefaultCacheDuration,
            Size = 1
        };

        // Single-writer pattern: MaxConcurrency=1 ensures serialized writes
        _writeCoordinator = new EphemeralWorkCoordinator<WriteCommand>(
            async (cmd, ct) => await ExecuteWriteInternalAsync(cmd, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = options.MaxTrackedWrites,
                Signals = _signals
            });
    }

    /// <summary>
    /// Executes a write command with serialized access. Samples write signals.
    /// </summary>
    public async Task<int> WriteAsync(string sql, object? parameters = null, CancellationToken ct = default)
    {
        var cmd = new WriteCommand(sql, parameters, ShouldSample());
        await _writeCoordinator.EnqueueAsync(cmd, ct);

        // Wait for this specific write to complete
        _writeCoordinator.Complete();
        await _writeCoordinator.DrainAsync(ct);

        return cmd.RowsAffected;
    }

    /// <summary>
    /// Executes a write and invalidates related cache keys via signals.
    /// </summary>
    public async Task<int> WriteAndInvalidateAsync(string sql, object? parameters = null,
        IEnumerable<string>? cacheKeysToInvalidate = null, CancellationToken ct = default)
    {
        var result = await WriteAsync(sql, parameters, ct);

        if (cacheKeysToInvalidate != null)
        {
            foreach (var key in cacheKeysToInvalidate)
            {
                _cache.Remove(key);
                _signals.Raise(new SignalEvent($"cache.invalidate:{key}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads data with caching. Emits cache hit/miss signals for observability.
    /// </summary>
    public async Task<T?> ReadAsync<T>(string cacheKey, Func<SqliteConnection, Task<T>> reader,
        MemoryCacheEntryOptions? cacheOptions = null, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(cacheKey, out T? cached))
        {
            if (ShouldSample())
                _signals.Raise(new SignalEvent($"cache.hit:{cacheKey}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
            return cached;
        }

        if (ShouldSample())
            _signals.Raise(new SignalEvent($"cache.miss:{cacheKey}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));

        // Read connections can be concurrent - SQLite handles this
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var result = await reader(connection);
        _cache.Set(cacheKey, result, cacheOptions ?? _defaultCacheOptions);

        return result;
    }

    /// <summary>
    /// Reads data without caching.
    /// </summary>
    public async Task<T> QueryAsync<T>(Func<SqliteConnection, Task<T>> reader, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return await reader(connection);
    }

    /// <summary>
    /// Gets recent signals (sampled write operations, cache hits/misses).
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals() => _signals.Sense();

    /// <summary>
    /// Gets signals matching a pattern (e.g., "write.*", "cache.*").
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(string pattern) =>
        _signals.Sense(s => StringPatternMatcher.Matches(s.Signal, pattern));

    /// <summary>
    /// Gets a snapshot of current write operations.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetWriteSnapshot() => _writeCoordinator.GetSnapshot();

    /// <summary>
    /// Invalidates cache and emits signal.
    /// </summary>
    public void InvalidateCache(string cacheKey)
    {
        _cache.Remove(cacheKey);
        _signals.Raise(new SignalEvent($"cache.invalidate:{cacheKey}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Waits for all pending writes to complete.
    /// </summary>
    public async Task FlushWritesAsync(CancellationToken ct = default)
    {
        _writeCoordinator.Complete();
        await _writeCoordinator.DrainAsync(ct);
    }

    private bool ShouldSample()
    {
        var count = Interlocked.Increment(ref _writeCount);
        return count % _sampleRate == 0;
    }

    private async Task ExecuteWriteInternalAsync(WriteCommand cmd, CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (cmd.EmitSignal)
            _signals.Raise(new SignalEvent($"write.start:{cmd.Sql[..Math.Min(30, cmd.Sql.Length)]}", EphemeralIdGenerator.NextId(), null, startTime));

        await _writeLock.WaitAsync(ct);
        try
        {
            _writeConnection ??= new SqliteConnection(_connectionString);

            if (_writeConnection.State != System.Data.ConnectionState.Open)
                await _writeConnection.OpenAsync(ct);

            await using var sqlCmd = _writeConnection.CreateCommand();
            sqlCmd.CommandText = cmd.Sql;

            if (cmd.Parameters != null)
                AddParameters(sqlCmd, cmd.Parameters);

            cmd.RowsAffected = await sqlCmd.ExecuteNonQueryAsync(ct);

            if (cmd.EmitSignal)
            {
                var duration = DateTimeOffset.UtcNow - startTime;
                _signals.Raise(new SignalEvent($"write.done:{cmd.RowsAffected}rows:{duration.TotalMilliseconds:F0}ms", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _signals.Raise(new SignalEvent($"write.error:{ex.Message[..Math.Min(50, ex.Message.Length)]}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void AddParameters(SqliteCommand cmd, object parameters)
    {
        var props = parameters.GetType().GetProperties();
        foreach (var prop in props)
        {
            var value = prop.GetValue(parameters);
            cmd.Parameters.AddWithValue($"@{prop.Name}", value ?? DBNull.Value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Instances.TryRemove(_connectionString, out _);
        await _writeCoordinator.DisposeAsync();

        if (_writeConnection != null)
            await _writeConnection.DisposeAsync();

        _writeLock.Dispose();
        _cache.Dispose();
    }

    private sealed class WriteCommand
    {
        public string Sql { get; }
        public object? Parameters { get; }
        public bool EmitSignal { get; }
        public int RowsAffected { get; set; }

        public WriteCommand(string sql, object? parameters, bool emitSignal)
        {
            Sql = sql;
            Parameters = parameters;
            EmitSignal = emitSignal;
        }
    }
}

/// <summary>
/// Configuration options for SqliteSingleWriter.
/// </summary>
public sealed class SqliteSingleWriterOptions
{
    /// <summary>
    /// Maximum number of items in the cache. Default: 1000.
    /// </summary>
    public long CacheSizeLimit { get; set; } = 1000;

    /// <summary>
    /// Default cache duration for read operations. Default: 5 minutes.
    /// </summary>
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of write operations to track. Default: 128.
    /// </summary>
    public int MaxTrackedWrites { get; set; } = 128;

    /// <summary>
    /// Signal sampling rate. 1 = every operation, 10 = every 10th. Default: 1.
    /// </summary>
    public int SampleRate { get; set; } = 1;
}
