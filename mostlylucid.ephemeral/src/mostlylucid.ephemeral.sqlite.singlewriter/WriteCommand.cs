using Microsoft.Data.Sqlite;

namespace Mostlylucid.Ephemeral.Sqlite;

internal sealed class WriteCommand
{
    private WriteCommand(
        Func<SqliteConnection, CancellationToken, Task<WriteCommandResult>> executor,
        string operation,
        bool emitSignal,
        Action<string>? signalEmitter)
    {
        Executor = executor;
        Operation = operation;
        EmitSignal = emitSignal;
        Signal = signalEmitter;
        Completion = new TaskCompletionSource<WriteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Func<SqliteConnection, CancellationToken, Task<WriteCommandResult>> Executor { get; }
    public string Operation { get; }
    public bool EmitSignal { get; }
    public Action<string>? Signal { get; }
    public TaskCompletionSource<WriteCommandResult> Completion { get; }

    public static WriteCommand ForSql(string sql, object? parameters, bool emitSignal, int commandTimeoutSeconds,
        string instanceId, Action<string>? signal)
    {
        return new WriteCommand(async (conn, ct) =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = commandTimeoutSeconds;
                if (parameters != null)
                    AddParameters(cmd, parameters);

                var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new WriteCommandResult(rows, null);
            }, $"write:{SqlPreview(sql)}:{instanceId}", emitSignal, signal);
    }

    public static WriteCommand ForBatch(IReadOnlyList<(string Sql, object? Parameters)> batch, bool transactional,
        bool emitSignal, int commandTimeoutSeconds, string instanceId, Action<string>? signal)
    {
        return new WriteCommand(async (conn, ct) =>
            {
                SqliteTransaction? transaction = null;
                if (transactional)
                    transaction = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

                var total = 0;
                try
                {
                    if (emitSignal)
                        signal?.Invoke("write.batch.tx.begin");

                    foreach (var (sql, parameters) in batch)
                    {
                        if (emitSignal)
                            signal?.Invoke($"write.batch.item.start:{SqlPreview(sql)}");

                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = sql;
                        cmd.CommandTimeout = commandTimeoutSeconds;
                        if (transaction != null)
                            cmd.Transaction = transaction;
                        if (parameters != null)
                            AddParameters(cmd, parameters);
                        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        total += rows;

                        if (emitSignal)
                            signal?.Invoke($"write.batch.item.done:{rows}:{SqlPreview(sql)}");
                    }

                    if (transaction != null)
                        await transaction.CommitAsync(ct).ConfigureAwait(false);
                    if (emitSignal)
                        signal?.Invoke("write.batch.tx.commit");
                }
                catch
                {
                    if (transaction != null)
                        await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    if (emitSignal)
                        signal?.Invoke("write.batch.tx.rollback");
                    throw;
                }
                finally
                {
                    if (transaction != null)
                        await transaction.DisposeAsync().ConfigureAwait(false);
                }

                return new WriteCommandResult(total, null);
            }, $"write:batch:{instanceId}", emitSignal, signal);
    }

    public static WriteCommand ForTransactional<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        bool emitSignal,
        string instanceId,
        Action<string>? signal)
    {
        return new WriteCommand(async (conn, ct) =>
            {
                await using var transaction =
                    (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    if (emitSignal)
                        signal?.Invoke("write.transaction.begin");

                    var result = await work(conn, transaction, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    if (emitSignal)
                        signal?.Invoke("write.transaction.commit");
                    return new WriteCommandResult(0, result);
                }
                catch
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    if (emitSignal)
                        signal?.Invoke("write.transaction.rollback");
                    throw;
                }
            }, $"write:transaction:{instanceId}", emitSignal, signal);
    }

    public static WriteCommand Barrier()
    {
        return new WriteCommand((_, _) => Task.FromResult(new WriteCommandResult(0, null)), "write:barrier", false,
            null);
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

    private static string SqlPreview(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "empty";
        return sql.Length <= 30 ? sql : sql[..30];
    }
}

internal readonly record struct WriteCommandResult(int RowsAffected, object? Result);