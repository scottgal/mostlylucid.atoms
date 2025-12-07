using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Mostlylucid.Ephemeral.Sqlite;
using Mostlylucid.Ephemeral;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mostlylucid.Ephemeral.Sqlite.Tests;

public class SqliteSingleWriterTests
{
    [Fact]
    public async Task WriteAsync_AllowsMultipleWritesAndReads()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await EnsureSchemaAsync(writer);

            var inserts = new[]
            {
                ("INSERT INTO Items (Name, Value) VALUES (@Name, @Value)", new { Name = "one", Value = 1 }),
                ("INSERT INTO Items (Name, Value) VALUES (@Name, @Value)", new { Name = "two", Value = 2 }),
                ("INSERT INTO Items (Name, Value) VALUES (@Name, @Value)", new { Name = "three", Value = 3 })
            };

            foreach (var (sql, parameters) in inserts)
            {
                await writer.WriteAsync(sql, parameters);
            }

            var count = await writer.QueryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });

            Assert.Equal(inserts.Length, count);

            var values = await writer.QueryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM Items ORDER BY Id";
                var list = new List<int>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(reader.GetInt32(0));
                }
                return list;
            });

            Assert.Equal(new[] { 1, 2, 3 }, values);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task ReadAsync_CachesUntilInvalidated()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await EnsureSchemaAsync(writer);
            await writer.WriteAsync("INSERT INTO Items (Name, Value) VALUES ('cached', 1)");

            var cacheKey = "items:count";
            var executionCount = 0;

            async Task<int> CountAsync(SqliteConnection conn)
            {
                Interlocked.Increment(ref executionCount);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            }

            var first = await writer.ReadAsync(cacheKey, CountAsync);
            var second = await writer.ReadAsync(cacheKey, CountAsync);

            Assert.Equal(1, executionCount);
            Assert.Equal(first, second);

            await writer.WriteAndInvalidateAsync(
                "INSERT INTO Items (Name, Value) VALUES ('new', 2)",
                cacheKeysToInvalidate: new[] { cacheKey });

            var third = await writer.ReadAsync(cacheKey, CountAsync);

            Assert.Equal(2, executionCount);
            Assert.Equal(2, third);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_RollsBackOnFailure()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await EnsureSchemaAsync(writer, uniqueName: true);

            var ex = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await writer.WriteBatchAsync(new[]
                {
                    ("INSERT INTO Items (Name, Value) VALUES ('dup', 1)", (object?)null),
                    ("INSERT INTO Items (Name, Value) VALUES ('dup', 2)", (object?)null) // Violates UNIQUE
                });
            });

            Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);

            var count = await writer.QueryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });

            Assert.Equal(0, count);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_NonTransactional_PartialCommitOnFailure()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await EnsureSchemaAsync(writer, uniqueName: true);

            await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await writer.WriteBatchAsync(new[]
                {
                    ("INSERT INTO Items (Name, Value) VALUES ('ok', 1)", (object?)null),
                    ("INSERT INTO Items (Name, Value) VALUES ('ok', 2)", (object?)null) // duplicate
                }, transactional: false);
            });

            var count = await writer.QueryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });

            // First insert succeeds even though batch failed
            Assert.Equal(1, count);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_ReturnsValueAndCommits()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await EnsureSchemaAsync(writer);

            var sum = await writer.ExecuteInTransactionAsync(async (conn, tx, ct) =>
            {
                await using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO Items (Name, Value) VALUES ('tx', 10)";
                await insert.ExecuteNonQueryAsync(ct);

                await using var sumCmd = conn.CreateCommand();
                sumCmd.Transaction = tx;
                sumCmd.CommandText = "SELECT SUM(Value) FROM Items";
                var result = await sumCmd.ExecuteScalarAsync(ct);
                return Convert.ToInt32(result ?? 0);
            });

            Assert.Equal(10, sum);

            var count = await writer.QueryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });

            Assert.Equal(1, count);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task ApplyPragmas_SetsWalAndEnforcesForeignKeys()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await writer.WriteAsync("""
CREATE TABLE IF NOT EXISTS Parents (Id INTEGER PRIMARY KEY);
CREATE TABLE IF NOT EXISTS Children (Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL, FOREIGN KEY(ParentId) REFERENCES Parents(Id));
""");

            // Foreign key should be enforced by PRAGMA foreign_keys=ON
            await Assert.ThrowsAsync<SqliteException>(async () =>
                await writer.WriteAsync("INSERT INTO Children (ParentId) VALUES (999)"));

            // WAL mode should be applied to the database
            var journalMode = await writer.QueryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode;";
                var mode = (string)(await cmd.ExecuteScalarAsync() ?? string.Empty);
                return mode.ToLowerInvariant();
            });

            Assert.Equal("wal", journalMode);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Signals_AreEmitted_ForLifecycleAndCache()
    {
        var dbPath = CreateDbPath();
        await using var writer = CreateWriter(dbPath);
        try
        {
            await EnsureSchemaAsync(writer);

            await writer.WriteAsync("INSERT INTO Items (Name, Value) VALUES ('sig', 5)");
            await writer.ReadAsync("items:count", async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });

            await writer.WriteAndInvalidateAsync(
                "INSERT INTO Items (Name, Value) VALUES ('sig2', 6)",
                cacheKeysToInvalidate: new[] { "items:count" });

            await writer.FlushWritesAsync();

            var signals = writer.GetSignals();
            Assert.Contains(signals, s => s.Signal.StartsWith("write:") && s.Signal.Contains(".start"));
            Assert.Contains(signals, s => s.Signal.StartsWith("write:") && s.Signal.Contains(".done"));
            Assert.Contains(signals, s => s.Signal == "write.flush.start");
            Assert.Contains(signals, s => s.Signal == "write.flush.done");
            Assert.Contains(signals, s => s.Signal.StartsWith("cache.miss:items:count"));
            Assert.Contains(signals, s => s.Signal.StartsWith("cache.set:items:count"));
            Assert.Contains(signals, s => s.Signal.StartsWith("cache.invalidate:items:count"));
            Assert.Contains(signals, s => s.Signal.StartsWith("read.start:items:count"));
            Assert.Contains(signals, s => s.Signal.StartsWith("read.done:items:count"));
            Assert.Contains(signals, s => s.Signal == "pragma.wal.on");
            Assert.Contains(signals, s => s.Signal == "pragma.foreign_keys.on");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task ExternalSignalInvalidation_RemovesCachedEntries()
    {
        var dbPath = CreateDbPath();
        var externalSink = new SignalSink();
        await using var writer = CreateWriter(dbPath);
        writer.EnableSignalDrivenInvalidation(externalSink);

        try
        {
            await EnsureSchemaAsync(writer);
            await writer.WriteAsync("INSERT INTO Items (Name, Value) VALUES ('first', 1)");

            var cacheKey = "items:count";
            var first = await writer.ReadAsync(cacheKey, async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });
            Assert.Equal(1, first);

            await writer.WriteAsync("INSERT INTO Items (Name, Value) VALUES ('second', 2)");

            // Raise external invalidation
            externalSink.Raise(new SignalEvent($"cache.invalidate:{cacheKey}", EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
            await Task.Delay(800); // Allow poll loop to process comfortably

            var second = await writer.ReadAsync(cacheKey, async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Items";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result ?? 0);
            });

            Assert.Equal(2, second);

            var signals = writer.GetSignals();
            Assert.Contains(signals, s => s.Signal.StartsWith("cache.invalidate.external:items:count"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    private static string CreateDbPath() =>
        Path.Combine(Path.GetTempPath(), $"ephemeral-sqlite-{Guid.NewGuid():N}.db");

    private static SqliteSingleWriter CreateWriter(string dbPath) =>
        SqliteSingleWriter.GetOrCreate(
            $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared",
            new SqliteSingleWriterOptions
            {
                SampleRate = 1,
                MaxTrackedWrites = 256
            });

    private static async Task EnsureSchemaAsync(SqliteSingleWriter writer, bool uniqueName = false)
    {
        var nameConstraint = uniqueName ? "TEXT NOT NULL UNIQUE" : "TEXT NOT NULL";
        await writer.WriteAsync($"""
CREATE TABLE IF NOT EXISTS Items (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name {nameConstraint},
    Value INTEGER NOT NULL
);
""");
    }

    private static void Cleanup(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
        catch
        {
            // Best-effort cleanup for temp file
        }
    }
}
