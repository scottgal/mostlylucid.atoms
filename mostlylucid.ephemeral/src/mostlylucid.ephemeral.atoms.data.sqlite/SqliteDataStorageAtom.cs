using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mostlylucid.Ephemeral.Atoms.Data.Sqlite;

/// <summary>
///     Configuration specific to SQLite storage.
/// </summary>
public class SqliteDataStorageConfig : DataStorageConfig
{
    /// <summary>
    ///     SQLite connection string. Default uses in-memory database.
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=:memory:";

    /// <summary>
    ///     Table name for storing data. Default is "data".
    /// </summary>
    public string TableName { get; set; } = "data";

    /// <summary>
    ///     Whether to use WAL journal mode for better concurrency. Default is true.
    /// </summary>
    public bool UseWalMode { get; set; } = true;

    /// <summary>
    ///     JSON serializer options.
    /// </summary>
    public JsonSerializerOptions? JsonOptions { get; set; }

    /// <summary>
    ///     Creates a file-based SQLite connection string.
    /// </summary>
    public static string FileConnectionString(string path)
    {
        return $"Data Source={path}";
    }
}

/// <summary>
///     SQLite data storage atom with ACID guarantees.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
/// <typeparam name="TValue">Type of the value (must be JSON-serializable).</typeparam>
public sealed class SqliteDataStorageAtom<TKey, TValue> : DataStorageAtomBase<TKey, TValue>
    where TKey : notnull
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SqliteDataStorageConfig _sqliteConfig;
    private SqliteConnection? _connection;
    private bool _initialized;

    public SqliteDataStorageAtom(SignalSink signals, SqliteDataStorageConfig config)
        : base(signals, config)
    {
        _sqliteConfig = config;
        _jsonOptions = config.JsonOptions ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    ///     Creates a SQLite storage atom with a file path.
    /// </summary>
    public SqliteDataStorageAtom(SignalSink signals, string databaseName, string dbPath)
        : this(signals, new SqliteDataStorageConfig
        {
            DatabaseName = databaseName,
            ConnectionString = SqliteDataStorageConfig.FileConnectionString(dbPath),
            TableName = databaseName
        })
    {
    }

    /// <summary>
    ///     Creates an in-memory SQLite storage atom.
    /// </summary>
    public SqliteDataStorageAtom(SignalSink signals, string databaseName)
        : this(signals, new SqliteDataStorageConfig
        {
            DatabaseName = databaseName,
            ConnectionString = "Data Source=:memory:;Mode=Memory;Cache=Shared",
            TableName = databaseName
        })
    {
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _connection = new SqliteConnection(_sqliteConfig.ConnectionString);
            await _connection.OpenAsync(ct).ConfigureAwait(false);

            if (_sqliteConfig.UseWalMode)
            {
                await using var pragmaCmd = _connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000;";
                await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {_sqliteConfig.TableName} (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_{_sqliteConfig.TableName}_updated
                    ON {_sqliteConfig.TableName}(updated_at);
            ";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
            Signals.Raise($"initialized.data.{Config.DatabaseName}");
        }
        finally
        {
            _initLock.Release();
        }
    }

    protected override async Task SaveInternalAsync(TKey key, TValue value, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(value, _jsonOptions);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {_sqliteConfig.TableName} (key, value, updated_at)
            VALUES (@key, @value, CURRENT_TIMESTAMP)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = CURRENT_TIMESTAMP;
        ";
        cmd.Parameters.AddWithValue("@key", key.ToString());
        cmd.Parameters.AddWithValue("@value", json);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected override async Task<TValue?> LoadInternalAsync(TKey key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"SELECT value FROM {_sqliteConfig.TableName} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key.ToString());

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull)
            return default;

        return JsonSerializer.Deserialize<TValue>((string)result, _jsonOptions);
    }

    protected override async Task DeleteInternalAsync(TKey key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_sqliteConfig.TableName} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key.ToString());

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected override async Task<bool> ExistsInternalAsync(TKey key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {_sqliteConfig.TableName} WHERE key = @key LIMIT 1";
        cmd.Parameters.AddWithValue("@key", key.ToString());

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>
    ///     Counts all entries.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_sqliteConfig.TableName}";

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long count ? count : 0;
    }

    /// <summary>
    ///     Lists all keys.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var keys = new List<string>();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"SELECT key FROM {_sqliteConfig.TableName} ORDER BY key";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetString(0));

        return keys;
    }

    /// <summary>
    ///     Clears all data.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_sqliteConfig.TableName}";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);

        if (_connection != null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _initLock.Dispose();
    }
}