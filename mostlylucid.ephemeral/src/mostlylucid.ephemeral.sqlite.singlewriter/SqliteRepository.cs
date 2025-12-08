using Microsoft.Data.Sqlite;

namespace Mostlylucid.Ephemeral.Sqlite;

/// <summary>
///     Base class for creating type-safe SQLite repositories with single-writer semantics.
/// </summary>
/// <typeparam name="TEntity">The entity type this repository manages.</typeparam>
/// <typeparam name="TKey">The primary key type.</typeparam>
public abstract class SqliteRepository<TEntity, TKey> : IAsyncDisposable
    where TEntity : class
{
    protected readonly string TableName;
    protected readonly SqliteSingleWriter Writer;

    protected SqliteRepository(string connectionString, string tableName, SqliteSingleWriterOptions? options = null)
    {
        Writer = SqliteSingleWriter.GetOrCreate(connectionString, options);
        TableName = tableName;
    }

    public virtual ValueTask DisposeAsync()
    {
        return Writer.DisposeAsync();
    }

    /// <summary>
    ///     Gets an entity by its primary key with caching.
    /// </summary>
    public virtual async Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default)
    {
        var cacheKey = GetEntityCacheKey(id);
        return await Writer.ReadAsync(cacheKey, async conn => await ReadEntityAsync(conn, id, ct), ct: ct);
    }

    /// <summary>
    ///     Gets all entities with caching.
    /// </summary>
    public virtual async Task<IReadOnlyList<TEntity>> GetAllAsync(CancellationToken ct = default)
    {
        var cacheKey = $"{TableName}:all";
        var result = await Writer.ReadAsync(cacheKey, async conn => await ReadAllEntitiesAsync(conn, ct), ct: ct);
        return result ?? Array.Empty<TEntity>();
    }

    /// <summary>
    ///     Inserts an entity and invalidates the "all" cache.
    /// </summary>
    public virtual async Task<int> InsertAsync(TEntity entity, CancellationToken ct = default)
    {
        var (sql, parameters) = BuildInsertCommand(entity);
        return await Writer.WriteAndInvalidateAsync(sql, parameters, new[] { $"{TableName}:all" }, ct);
    }

    /// <summary>
    ///     Updates an entity and invalidates relevant caches.
    /// </summary>
    public virtual async Task<int> UpdateAsync(TKey id, TEntity entity, CancellationToken ct = default)
    {
        var (sql, parameters) = BuildUpdateCommand(id, entity);
        var keysToInvalidate = new[] { GetEntityCacheKey(id), $"{TableName}:all" };
        return await Writer.WriteAndInvalidateAsync(sql, parameters, keysToInvalidate, ct);
    }

    /// <summary>
    ///     Deletes an entity and invalidates relevant caches.
    /// </summary>
    public virtual async Task<int> DeleteAsync(TKey id, CancellationToken ct = default)
    {
        var sql = $"DELETE FROM {TableName} WHERE Id = @Id";
        var keysToInvalidate = new[] { GetEntityCacheKey(id), $"{TableName}:all" };
        return await Writer.WriteAndInvalidateAsync(sql, new { Id = id }, keysToInvalidate, ct);
    }

    /// <summary>
    ///     Executes a custom query without caching.
    /// </summary>
    public async Task<T> QueryAsync<T>(Func<SqliteConnection, Task<T>> query, CancellationToken ct = default)
    {
        return await Writer.QueryAsync(query, ct);
    }

    /// <summary>
    ///     Executes a custom write command.
    /// </summary>
    public async Task<int> ExecuteAsync(string sql, object? parameters = null,
        IEnumerable<string>? cacheKeysToInvalidate = null, CancellationToken ct = default)
    {
        return await Writer.WriteAndInvalidateAsync(sql, parameters, cacheKeysToInvalidate, ct);
    }

    /// <summary>
    ///     Generates a cache key for an entity.
    /// </summary>
    protected virtual string GetEntityCacheKey(TKey id)
    {
        return $"{TableName}:{id}";
    }

    /// <summary>
    ///     Reads a single entity from the database. Override to customize.
    /// </summary>
    protected abstract Task<TEntity?> ReadEntityAsync(SqliteConnection connection, TKey id, CancellationToken ct);

    /// <summary>
    ///     Reads all entities from the database. Override to customize.
    /// </summary>
    protected abstract Task<IReadOnlyList<TEntity>> ReadAllEntitiesAsync(SqliteConnection connection,
        CancellationToken ct);

    /// <summary>
    ///     Builds the INSERT SQL and parameters. Override to customize.
    /// </summary>
    protected abstract (string sql, object parameters) BuildInsertCommand(TEntity entity);

    /// <summary>
    ///     Builds the UPDATE SQL and parameters. Override to customize.
    /// </summary>
    protected abstract (string sql, object parameters) BuildUpdateCommand(TKey id, TEntity entity);
}