using System.Text.Json;
using Npgsql;

namespace Mostlylucid.Ephemeral.Atoms.Data.Postgres;

/// <summary>
///     Configuration specific to PostgreSQL storage.
/// </summary>
public class PostgresDataStorageConfig : DataStorageConfig
{
    /// <summary>
    ///     PostgreSQL connection string.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    ///     Schema name. Default is "public".
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    ///     Table name for storing data. Default is "data".
    /// </summary>
    public string TableName { get; set; } = "data";

    /// <summary>
    ///     Whether to use JSONB type (recommended). Default is true.
    /// </summary>
    public bool UseJsonb { get; set; } = true;

    /// <summary>
    ///     JSON serializer options.
    /// </summary>
    public JsonSerializerOptions? JsonOptions { get; set; }

    /// <summary>
    ///     Full qualified table name (schema.table).
    /// </summary>
    public string FullTableName => $"{Schema}.{TableName}";
}

/// <summary>
///     PostgreSQL data storage atom with native JSONB support.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
/// <typeparam name="TValue">Type of the value (must be JSON-serializable).</typeparam>
public sealed class PostgresDataStorageAtom<TKey, TValue> : DataStorageAtomBase<TKey, TValue>
    where TKey : notnull
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly PostgresDataStorageConfig _pgConfig;
    private bool _initialized;

    public PostgresDataStorageAtom(SignalSink signals, PostgresDataStorageConfig config)
        : base(signals, config)
    {
        _pgConfig = config;
        _jsonOptions = config.JsonOptions ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        _dataSource = NpgsqlDataSource.Create(config.ConnectionString);
    }

    /// <summary>
    ///     Creates a PostgreSQL storage atom with a connection string.
    /// </summary>
    public PostgresDataStorageAtom(SignalSink signals, string databaseName, string connectionString)
        : this(signals, new PostgresDataStorageConfig
        {
            DatabaseName = databaseName,
            ConnectionString = connectionString,
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

            await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

            var jsonType = _pgConfig.UseJsonb ? "JSONB" : "JSON";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {_pgConfig.FullTableName} (
                    key TEXT PRIMARY KEY NOT NULL,
                    value {jsonType} NOT NULL,
                    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_{_pgConfig.TableName}_updated
                    ON {_pgConfig.FullTableName}(updated_at);
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

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {_pgConfig.FullTableName} (key, value, updated_at)
            VALUES (@key, @value::jsonb, CURRENT_TIMESTAMP)
            ON CONFLICT (key) DO UPDATE SET
                value = EXCLUDED.value,
                updated_at = CURRENT_TIMESTAMP;
        ";
        cmd.Parameters.AddWithValue("@key", key.ToString()!);
        cmd.Parameters.AddWithValue("@value", json);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected override async Task<TValue?> LoadInternalAsync(TKey key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT value::text FROM {_pgConfig.FullTableName} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key.ToString()!);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull)
            return default;

        return JsonSerializer.Deserialize<TValue>((string)result, _jsonOptions);
    }

    protected override async Task DeleteInternalAsync(TKey key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_pgConfig.FullTableName} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key.ToString()!);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected override async Task<bool> ExistsInternalAsync(TKey key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {_pgConfig.FullTableName} WHERE key = @key LIMIT 1";
        cmd.Parameters.AddWithValue("@key", key.ToString()!);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>
    ///     Counts all entries.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_pgConfig.FullTableName}";

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT key FROM {_pgConfig.FullTableName} ORDER BY key";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetString(0));

        return keys;
    }

    /// <summary>
    ///     Queries by JSON path (PostgreSQL JSONB feature).
    /// </summary>
    public async Task<IReadOnlyList<TValue>> QueryByJsonPathAsync(string jsonPath, object value,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var results = new List<TValue>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT value::text FROM {_pgConfig.FullTableName}
            WHERE value @> @pattern::jsonb
        ";

        var pattern = $"{{\"{jsonPath}\": {JsonSerializer.Serialize(value)}}}";
        cmd.Parameters.AddWithValue("@pattern", pattern);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = JsonSerializer.Deserialize<TValue>(json, _jsonOptions);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    /// <summary>
    ///     Clears all data.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE {_pgConfig.FullTableName}";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _initLock.Dispose();
    }
}