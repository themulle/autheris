using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-ARC-B-13: pooled per-operation SQLite connections instead of one shared connection.</summary>
public sealed class SqliteGovernanceConnectionProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "autheris-sqlite-provider-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string DatabaseFile => Path.Combine(_directory, "sub", "governance.db");

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task FileDatabase_NeverUsesSharedCache()
    {
        await using var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile};Cache=Shared", "WAL", null);

        await using var connection = await provider.OpenAsync(CancellationToken.None);

        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        builder.Cache.ShouldBe(SqliteCacheMode.Default);
        builder.Pooling.ShouldBeTrue();
        provider.IsMemory.ShouldBeFalse();
    }

    [Fact]
    public async Task FileDatabase_CreatesTheMissingDirectoryOnFirstOpen()
    {
        await using var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", "WAL", null);
        Directory.Exists(Path.GetDirectoryName(DatabaseFile)!).ShouldBeFalse("the constructor performs no I/O");

        await using var connection = await provider.OpenAsync(CancellationToken.None);

        File.Exists(DatabaseFile).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Data Source=prov_{0};Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=:memory:;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=prov_{0};Mode=Memory")]
    public async Task InMemoryDatabase_PersistsAcrossOperations(string template)
    {
        await using var provider = new SqliteGovernanceConnectionProvider(string.Format(template, Guid.NewGuid().ToString("N")), "WAL", null);
        provider.IsMemory.ShouldBeTrue();

        await using (var first = await provider.OpenAsync(CancellationToken.None))
        {
            await ExecAsync(first, "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (7);");
        }

        await using var second = await provider.OpenAsync(CancellationToken.None);
        (await ScalarAsync(second, "SELECT id FROM t")).ShouldBe("7");
    }

    [Fact]
    public async Task InMemoryDatabases_OfTwoProviders_AreIsolated()
    {
        await using var a = new SqliteGovernanceConnectionProvider("Data Source=:memory:", "WAL", null);
        await using var b = new SqliteGovernanceConnectionProvider("Data Source=:memory:", "WAL", null);

        await using (var conn = await a.OpenAsync(CancellationToken.None))
        {
            await ExecAsync(conn, "CREATE TABLE only_a (id INTEGER);");
        }

        await using var other = await b.OpenAsync(CancellationToken.None);
        (await ScalarAsync(other, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'only_a'")).ShouldBe("0");
    }

    [Theory]
    [InlineData("WAL", "wal", "1")]
    [InlineData("DELETE", "delete", "2")]
    [InlineData("nonsense", "wal", "1")]
    [InlineData(null, "wal", "1")]
    public async Task JournalMode_IsAppliedOnFirstOpen_WithMatchingSynchronous(string? configured, string expectedMode, string expectedSynchronous)
    {
        await using var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", configured, null);

        await using var connection = await provider.OpenAsync(CancellationToken.None);

        (await ScalarAsync(connection, "PRAGMA journal_mode")).ShouldBe(expectedMode);
        (await ScalarAsync(connection, "PRAGMA synchronous")).ShouldBe(expectedSynchronous);
    }

    [Fact]
    public async Task EveryConnection_GetsTheOperationalPragmas()
    {
        await using var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", "WAL", null);

        for (var i = 0; i < 2; i++)
        {
            await using var connection = await provider.OpenAsync(CancellationToken.None);
            (await ScalarAsync(connection, "PRAGMA busy_timeout")).ShouldBe("5000");
            (await ScalarAsync(connection, "PRAGMA temp_store")).ShouldBe("2");
            (await ScalarAsync(connection, "PRAGMA cache_size")).ShouldBe("-8000");
        }
    }

    [Fact]
    public async Task JournalMode_FallsBackToDelete_WhenWalCannotBeApplied()
    {
        await using var provider = new SqliteGovernanceConnectionProvider(
            $"Data Source={DatabaseFile}",
            "WAL",
            null,
            journalModeApplier: (connection, mode) =>
            {
                if (mode == "WAL")
                {
                    throw new SqliteException("disk I/O error", 10);
                }

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA journal_mode = {mode};";
                return Convert.ToString(cmd.ExecuteScalar())!;
            });

        await using var connection = await provider.OpenAsync(CancellationToken.None);

        (await ScalarAsync(connection, "PRAGMA journal_mode")).ShouldBe("delete");
        (await ScalarAsync(connection, "PRAGMA synchronous")).ShouldBe("2");
    }

    [Fact]
    public async Task Readers_AreNotBlocked_ByAnOpenWriteTransaction_InWalMode()
    {
        await using var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", "WAL", null);
        await using (var setup = await provider.OpenAsync(CancellationToken.None))
        {
            await ExecAsync(setup, "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);");
        }

        await using var writer = await provider.OpenAsync(CancellationToken.None);
        await using var tx = await writer.BeginTransactionAsync();
        await ExecAsync(writer, "UPDATE t SET id = 2;");

        await using var reader = await provider.OpenAsync(CancellationToken.None);
        var read = ScalarAsync(reader, "SELECT id FROM t");
        (await read.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe("1", "a reader sees the committed state without waiting for the writer");
    }

    [Fact]
    public async Task Dispose_ReleasesTheFile_AndRemovesTheWalSideFiles()
    {
        var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", "WAL", null);
        await using (var connection = await provider.OpenAsync(CancellationToken.None))
        {
            await ExecAsync(connection, "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);");
        }

        File.Exists(DatabaseFile + "-wal").ShouldBeTrue("a pooled connection keeps the WAL open");

        await provider.DisposeAsync();

        File.Exists(DatabaseFile + "-wal").ShouldBeFalse("disposing the provider clears the pool and checkpoints the WAL");
        File.Exists(DatabaseFile + "-shm").ShouldBeFalse();
        File.Delete(DatabaseFile);
    }

    [Fact]
    public async Task OpenAfterDispose_Throws()
    {
        var provider = new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", "WAL", null);
        await provider.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await provider.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EffectiveJournalMode_IsReadBack_WhenTheFilesystemKeepsDelete()
    {
        // The pragma answers "delete" instead of the requested "wal" (no shared memory): synchronous must follow the effective mode.
        await using var provider = new SqliteGovernanceConnectionProvider(
            $"Data Source={DatabaseFile}",
            "WAL",
            null,
            journalModeApplier: (connection, mode) => "delete");

        await using var connection = await provider.OpenAsync(CancellationToken.None);

        (await ScalarAsync(connection, "PRAGMA synchronous")).ShouldBe("2");
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData("memory")]
    public void UnsafeJournalModes_AreRejected_OutsideDevelopment(string mode)
    {
        Should.Throw<InvalidOperationException>(() =>
            new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", mode, null, allowUnsafeJournalModes: false));

        Should.NotThrow(() => new SqliteGovernanceConnectionProvider($"Data Source={DatabaseFile}", mode, null, allowUnsafeJournalModes: true).Dispose());
    }

    // I388-SC-09 regression: opening connections while another connection holds an uncommitted schema write (a migrator running DDL)
    // must not fail. The shared-cache in-memory database used to throw ArgumentOutOfRangeException from sqlite3_prepare_v2 in the pragma batch.
    [Fact]
    public async Task MemoryDatabase_OpenWhileAnotherConnectionHoldsUncommittedDdl_DoesNotFail()
    {
        await using var provider = new SqliteGovernanceConnectionProvider("Data Source=prov_ddl_" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared", null, null);
        await using var writer = await provider.OpenAsync(CancellationToken.None);
        await ExecAsync(writer, "BEGIN IMMEDIATE; CREATE TABLE ddl_probe (id TEXT NOT NULL PRIMARY KEY);");
        try
        {
            for (var round = 0; round < 20; round++)
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var opens = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    await using var connection = await provider.OpenAsync(CancellationToken.None);
                })).ToArray();
                start.SetResult();
                await Task.WhenAll(opens);
            }
        }
        finally
        {
            await ExecAsync(writer, "ROLLBACK;");
        }
    }
}
