using Microsoft.Data.Sqlite;

namespace Mostlylucid.Ephemeral.Patterns.PersistentWindow;

/// <summary>
/// A signal window that periodically persists to SQLite and can restore on restart.
/// Demonstrates combining Ephemeral coordination with SQLite single-writer pattern.
/// </summary>
public sealed class PersistentSignalWindow : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SignalSink _sink;
    private readonly EphemeralWorkCoordinator<PersistCommand> _writeCoordinator;
    private readonly TimeSpan _flushInterval;
    private readonly int _maxSignalsPerFlush;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SqliteConnection? _writeConnection;
    private bool _initialized;
    private readonly HashSet<long> _flushedIds = new();
    private long _lastFlushedId; // For stats/backward compat
    private int _signalCount;
    private readonly int _sampleRate;

    /// <summary>
    /// Creates a persistent signal window.
    /// </summary>
    /// <param name="connectionString">SQLite connection string</param>
    /// <param name="flushInterval">How often to flush to disk (default: 30 seconds)</param>
    /// <param name="maxSignalsPerFlush">Max signals to persist per flush (default: 1000)</param>
    /// <param name="maxWindowSize">Max signals in memory (default: 10000)</param>
    /// <param name="windowMaxAge">Max age of signals in memory (default: 10 minutes)</param>
    /// <param name="sampleRate">Signal sampling for diagnostics (default: 10)</param>
    public PersistentSignalWindow(
        string connectionString,
        TimeSpan? flushInterval = null,
        int maxSignalsPerFlush = 1000,
        int maxWindowSize = 10000,
        TimeSpan? windowMaxAge = null,
        int sampleRate = 10)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(30);
        _maxSignalsPerFlush = maxSignalsPerFlush;
        _sampleRate = Math.Max(1, sampleRate);

        _sink = new SignalSink(
            maxCapacity: maxWindowSize,
            maxAge: windowMaxAge ?? TimeSpan.FromMinutes(10));

        // Single-writer for SQLite
        _writeCoordinator = new EphemeralWorkCoordinator<PersistCommand>(
            ExecuteCommandAsync,
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = 100,
                Signals = _sink
            });

        _flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>
    /// Raises a signal to the window.
    /// </summary>
    public void Raise(string signal, string? key = null)
    {
        var evt = new SignalEvent(signal, EphemeralIdGenerator.NextId(), key, DateTimeOffset.UtcNow);
        _sink.Raise(evt);

        if (ShouldSample())
            _sink.Raise(new SignalEvent("window.raise", evt.OperationId, null, DateTimeOffset.UtcNow));

        Interlocked.Increment(ref _signalCount);
    }

    /// <summary>
    /// Raises a signal event to the window.
    /// </summary>
    public void Raise(SignalEvent evt)
    {
        _sink.Raise(evt);
        Interlocked.Increment(ref _signalCount);
    }

    /// <summary>
    /// Queries signals matching a pattern.
    /// </summary>
    public IReadOnlyList<SignalEvent> Sense(string? pattern = null)
    {
        if (string.IsNullOrEmpty(pattern))
            return _sink.Sense();

        return _sink.Sense(s => StringPatternMatcher.Matches(s.Signal, pattern));
    }

    /// <summary>
    /// Queries signals with a predicate.
    /// </summary>
    public IReadOnlyList<SignalEvent> Sense(Func<SignalEvent, bool> predicate) =>
        _sink.Sense(predicate);

    /// <summary>
    /// Forces an immediate flush to SQLite.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var command = new PersistCommand(PersistCommandType.Flush, new TaskCompletionSource());
        await _writeCoordinator.EnqueueAsync(command, ct).ConfigureAwait(false);
        await command.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads signals from SQLite into the window.
    /// Call this on startup to restore previous state.
    /// </summary>
    public async Task LoadFromDiskAsync(TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var command = new PersistCommand(PersistCommandType.Load, new TaskCompletionSource(), maxAge);
        await _writeCoordinator.EnqueueAsync(command, ct).ConfigureAwait(false);
        await command.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets window statistics.
    /// </summary>
    public WindowStats GetStats()
    {
        var signals = _sink.Sense();
        return new WindowStats(
            InMemoryCount: signals.Count,
            TotalRaised: _signalCount,
            LastFlushedId: _lastFlushedId);
    }

    /// <summary>
    /// Gets the underlying signal sink for advanced usage.
    /// </summary>
    public SignalSink Sink => _sink;

    private async Task FlushLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, _cts.Token).ConfigureAwait(false);
                await FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                // Emit error signal so callers can observe and react
                _sink.Raise(new SignalEvent(
                    $"window.flush.loop.error:{ex.GetType().Name}",
                    EphemeralIdGenerator.NextId(),
                    null,
                    DateTimeOffset.UtcNow));
            }
        }
    }

    private async Task ExecuteCommandAsync(PersistCommand command, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);

            switch (command.Type)
            {
                case PersistCommandType.Flush:
                    await FlushInternalAsync(ct).ConfigureAwait(false);
                    break;
                case PersistCommandType.Load:
                    await LoadInternalAsync(command.MaxAge, ct).ConfigureAwait(false);
                    break;
            }

            command.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            command.Completion.TrySetException(ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        _writeConnection = new SqliteConnection(_connectionString);
        await _writeConnection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=10000;

            CREATE TABLE IF NOT EXISTS signals (
                id INTEGER PRIMARY KEY,
                operation_id INTEGER NOT NULL,
                signal TEXT NOT NULL,
                key TEXT,
                timestamp TEXT NOT NULL,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_signals_timestamp ON signals(timestamp);
            CREATE INDEX IF NOT EXISTS idx_signals_signal ON signals(signal);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _initialized = true;

        _sink.Raise(new SignalEvent("window.initialized", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
    }

    private async Task FlushInternalAsync(CancellationToken ct)
    {
        // Use HashSet to track flushed IDs since EphemeralIdGenerator uses hash-based IDs (not sequential)
        var signals = _sink.Sense()
            .Where(s => !_flushedIds.Contains(s.OperationId))
            .OrderBy(s => s.Timestamp) // Order by timestamp since IDs aren't sequential
            .Take(_maxSignalsPerFlush)
            .ToList();

        if (signals.Count == 0) return;

        _sink.Raise(new SignalEvent($"window.flush.start:{signals.Count}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));

        await using var tx = await _writeConnection!.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _writeConnection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO signals (operation_id, signal, key, timestamp) VALUES (@op, @sig, @key, @ts)";

            var opParam = cmd.Parameters.Add("@op", SqliteType.Integer);
            var sigParam = cmd.Parameters.Add("@sig", SqliteType.Text);
            var keyParam = cmd.Parameters.Add("@key", SqliteType.Text);
            var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);

            foreach (var signal in signals)
            {
                opParam.Value = signal.OperationId;
                sigParam.Value = signal.Signal;
                keyParam.Value = signal.Key ?? (object)DBNull.Value;
                tsParam.Value = signal.Timestamp.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _flushedIds.Add(signal.OperationId);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            _lastFlushedId = signals[^1].OperationId;

            _sink.Raise(new SignalEvent($"window.flush.done:{signals.Count}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            _sink.Raise(new SignalEvent("window.flush.error", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
            throw;
        }
    }

    private async Task LoadInternalAsync(TimeSpan? maxAge, CancellationToken ct)
    {
        var cutoff = maxAge.HasValue
            ? DateTimeOffset.UtcNow - maxAge.Value
            : DateTimeOffset.MinValue;

        await using var cmd = _writeConnection!.CreateCommand();
        cmd.CommandText = "SELECT operation_id, signal, key, timestamp FROM signals WHERE timestamp >= @cutoff ORDER BY timestamp";
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

        var loaded = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var opId = reader.GetInt64(0);
            var signal = reader.GetString(1);
            var source = reader.IsDBNull(2) ? null : reader.GetString(2);
            var timestamp = DateTimeOffset.Parse(reader.GetString(3));

            var evt = new SignalEvent(signal, opId, source, timestamp);
            _sink.Raise(evt);

            // Track as already flushed so we don't flush again
            _flushedIds.Add(opId);
            _lastFlushedId = opId;

            loaded++;
        }

        _sink.Raise(new SignalEvent($"window.load.done:{loaded}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
    }

    private bool ShouldSample()
    {
        return _signalCount % _sampleRate == 0;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _flushLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();

        // Final flush
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { }

        _writeCoordinator.Complete();
        await _writeCoordinator.DrainAsync().ConfigureAwait(false);
        await _writeCoordinator.DisposeAsync().ConfigureAwait(false);

        if (_writeConnection != null)
            await _writeConnection.DisposeAsync().ConfigureAwait(false);

        _writeLock.Dispose();
    }

    private enum PersistCommandType { Flush, Load }

    private readonly record struct PersistCommand(
        PersistCommandType Type,
        TaskCompletionSource Completion,
        TimeSpan? MaxAge = null);
}

/// <summary>
/// Window statistics.
/// </summary>
public readonly record struct WindowStats(
    int InMemoryCount,
    int TotalRaised,
    long LastFlushedId);
