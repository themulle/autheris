using System.Data.Common;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

/// <summary>Step 0031 on PostgreSQL and SQL Server: the profile masking mode and table entries move into item rows; an unmappable row aborts the whole step.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileItemsMigrationStep0031Tests
{
    private const string StepName = "access_profile_items";

    private const string PostgreSqlShape = """
        CREATE SCHEMA pre_step;
        SET search_path TO pre_step;
        CREATE TABLE access_profiles (
            profile_id VARCHAR(128) NOT NULL, tenant_id VARCHAR(64) NOT NULL, name VARCHAR(256) NOT NULL,
            masking_mode VARCHAR(32) NULL DEFAULT 'Default', target_tables TEXT NULL DEFAULT '["*.*"]',
            PRIMARY KEY (tenant_id, profile_id));
        """;

    private const string SqlServerShape = """
        CREATE TABLE dbo.ACCESS_PROFILES (
            profile_id NVARCHAR(128) NOT NULL, tenant_id NVARCHAR(64) NOT NULL, name NVARCHAR(256) NOT NULL,
            masking_mode NVARCHAR(32) NULL CONSTRAINT DF_ACCESS_PROFILES_mode DEFAULT 'Default',
            target_tables NVARCHAR(MAX) NULL CONSTRAINT DF_ACCESS_PROFILES_tables DEFAULT '["*.*"]',
            CONSTRAINT PK_ACCESS_PROFILES PRIMARY KEY (tenant_id, profile_id))
        """;

    private static async Task<Db?> OpenAsync(string engine, bool withPreShape)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 31 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("profile_items_0031"));
            await conn.OpenAsync();
            if (withPreShape)
            {
                await ExecAsync(conn, PostgreSqlShape);
            }

            return new Db(engine, conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        if (withPreShape)
        {
            await ExecAsync(sqlConn, SqlServerShape);
        }

        return new Db(engine, sqlConn, step, async () =>
        {
            await sqlConn.DisposeAsync();
            await database.DisposeAsync();
        });
    }

    private static string Profiles(string engine) => engine == "postgresql" ? "access_profiles" : "dbo.ACCESS_PROFILES";

    private static string Table(string engine, string name) => engine == "postgresql" ? name : "dbo." + name.ToUpperInvariant();

    private static async Task InsertProfileAsync(Db db, string tenant, string id, string? mode, string? tables)
    {
        await using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO {Profiles(db.Engine)} (profile_id, tenant_id, name, masking_mode, target_tables) VALUES (@id, @tenant, @id, @mode, @tables)";
        foreach (var (name, value) in new (string, object?)[] { ("@id", id), ("@tenant", tenant), ("@mode", mode), ("@tables", tables) })
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RunStepAsync(Db db)
    {
        foreach (var batch in db.Step.Batches)
        {
            await ExecAsync(db.Connection, batch);
        }
    }

    private static Task<long> ColumnCountAsync(Db db) => ScalarAsync(db.Connection, db.Engine == "postgresql"
        ? "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'access_profiles' AND column_name IN ('masking_mode', 'target_tables')"
        : "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ACCESS_PROFILES') AND name IN (N'masking_mode', N'target_tables')");

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Step0031_Backfill_MapsModesAndDedupes(string engine)
    {
        await using var db = await OpenAsync(engine, withPreShape: true);
        if (db is null) return;
        await InsertProfileAsync(db, "t1", "p-unmasked", "unmasked", "[\"tem.*\",\"md.x\",\"tem.*\"]");
        await InsertProfileAsync(db, "t1", "p-default", "Default", "[\"z.b\",\"a.b\"]");
        await InsertProfileAsync(db, "t2", "p-strict", "STRICT", "[]");
        await InsertProfileAsync(db, "t2", "p-default", "Strict", "[\"Case.X\",\"case.x\"]");

        await RunStepAsync(db);

        var c = db.Connection;
        var levels = Table(engine, "access_profile_classification_access");
        var items = Table(engine, "access_profile_items");
        (await ScalarAsync(c, $"SELECT access_level FROM {levels} WHERE tenant_id = 't1' AND profile_id = 'p-unmasked' AND code = 'PII'")).ShouldBe(2);
        (await ScalarAsync(c, $"SELECT access_level FROM {levels} WHERE tenant_id = 't1' AND profile_id = 'p-default' AND code = 'PII'")).ShouldBe(1);
        (await ScalarAsync(c, $"SELECT access_level FROM {levels} WHERE tenant_id = 't2' AND profile_id = 'p-strict' AND code = 'PII'")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT access_level FROM {levels} WHERE tenant_id = 't2' AND profile_id = 'p-default' AND code = 'PII'")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {items} WHERE profile_id = 'p-unmasked'")).ShouldBe(2);
        (await ScalarAsync(c, $"SELECT item_no FROM {items} WHERE profile_id = 'p-unmasked' AND object_ref = 'md.x'")).ShouldBe(2);
        (await ScalarAsync(c, $"SELECT item_no FROM {items} WHERE tenant_id = 't1' AND profile_id = 'p-default' AND object_ref = 'z.b'")).ShouldBe(1);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {items} WHERE profile_id = 'p-strict'")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {items} WHERE tenant_id = 't2' AND profile_id = 'p-default'")).ShouldBe(2);
        (await ColumnCountAsync(db)).ShouldBe(0);
    }

    private static readonly (string? Mode, string? Tables)[] BadCases =
    [
        ("Bogus", "[\"a.b\"]"),
        ("Default", "not json"),
        ("Default", "\"a.b\""),
        ("Default", "[\"a.b\", 5]"),
        ("Default", "[\"a.b\", null]"),
        ("Default", "[\"a.b\", \"   \"]"),
        ("Default", "OVERLONG"),
        ("Default", "[\"a\\u0000b\"]"),
        (null, "[\"a.b\"]"),
        ("Default", null)
    ];

    public static TheoryData<string, string?, string?> BadRows()
    {
        var data = new TheoryData<string, string?, string?>();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            foreach (var (mode, tables) in BadCases)
            {
                if (engine == "sqlserver" && tables is not null && tables.Contains("u0000", StringComparison.Ordinal))
                {
                    continue;
                }

                data.Add(engine, mode, tables);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BadRows))]
    public async Task Step0031_BadRow_AbortsAndLeavesColumnsIntact(string engine, string? mode, string? tables)
    {
        await using var db = await OpenAsync(engine, withPreShape: true);
        if (db is null) return;
        var bad = tables == "OVERLONG" ? "[\"" + new string('x', 513) + "\"]" : tables;
        await InsertProfileAsync(db, "t1", "p-good", "Default", "[\"a.b\"]");
        await InsertProfileAsync(db, "t1", "p-bad", mode, bad);

        var failure = await Should.ThrowAsync<DbException>(() => RunStepAsync(db));

        failure.Message.ShouldContain("MIG-0031");
        failure.Message.ShouldContain("p-bad");
        failure.Message.ShouldNotContain("not json");
        failure.Message.ShouldNotContain("xxxxx");
        (await ColumnCountAsync(db)).ShouldBe(2);
        (await ScalarAsync(db.Connection, $"SELECT COUNT(*) FROM {Table(engine, "access_profile_items")}")).ShouldBe(0);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Step0031_FreshInstallMatchesShape(string engine)
    {
        await using var db = await OpenAsync(engine, withPreShape: false);
        if (db is null) return;
        var c = db.Connection;

        if (engine == "postgresql")
        {
            (await ScalarAsync(c, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'access_profiles' AND column_name IN ('masking_mode', 'target_tables')")).ShouldBe(0);
            (await ScalarAsync(c, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'access_profile_items' AND column_name IN ('tenant_id', 'profile_id', 'item_no', 'object_ref')")).ShouldBe(4);
            (await ScalarAsync(c, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'access_profile_classification_access' AND column_name IN ('tenant_id', 'profile_id', 'code', 'access_level')")).ShouldBe(4);
        }
        else
        {
            // A fresh SQL Server database holds the frozen base plus the steps only when a runner applied them; check the step shape on the base tables.
            await RunBaselineAndStepAsync(db);
            (await ScalarAsync(c, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ACCESS_PROFILES') AND name IN (N'masking_mode', N'target_tables')")).ShouldBe(0);
            (await ScalarAsync(c, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ACCESS_PROFILE_ITEMS') AND name IN (N'tenant_id', N'profile_id', N'item_no', N'object_ref')")).ShouldBe(4);
            (await ScalarAsync(c, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ACCESS_PROFILE_CLASSIFICATION_ACCESS') AND name IN (N'tenant_id', N'profile_id', N'code', N'access_level')")).ShouldBe(4);
            (await ScalarAsync(c, "SELECT COUNT(*) FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.ACCESS_PROFILES') AND name IN (N'DF_ACCESS_PROFILES_mode', N'DF_ACCESS_PROFILES_tables')")).ShouldBe(0);
        }
    }

    private static async Task RunBaselineAndStepAsync(Db db)
    {
        // Baseline default constraints of the two columns (as created by the frozen v1.5.0 base), then the step on an empty table.
        await ExecAsync(db.Connection, SqlServerShape);
        await RunStepAsync(db);
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed record Db(string Engine, DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }
}
