namespace Autheris.Tests.Integration;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>Issue #94, plan D-2/D-3 and SEC-94-08d on PostgreSQL: snapshot equality, Verify, pre-created drifted tables, shadow tables.</summary>
public sealed class PostgreSqlGovernanceSchemaShapeTests : IClassFixture<PostgreSqlGovernanceSchemaShapeTests.MigratedDatabase>
{
    /// <summary>One migrated database shared by the tests that only read the schema.</summary>
    public sealed class MigratedDatabase : IAsyncLifetime
    {
        public PostgreSqlTestDatabase Db { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Db = await PostgreSqlTestDatabase.CreateAsync();
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(Db.ConnectionString, GovernanceMigrationMode.Apply);
        }

        public async Task DisposeAsync() => await Db.DisposeAsync();
    }

    private readonly MigratedDatabase _shared;

    public PostgreSqlGovernanceSchemaShapeTests(MigratedDatabase shared) => _shared = shared;

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EmbeddedSnapshot_EqualsShapeOfTheBaseline()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync(); // a fresh database: the shared one may already hold migrated steps
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            // The baseline DDL is applied directly: the runner itself depends on the snapshot that this test generates.
            PostgreSqlBaselineSchema.Apply(conn, null);
            var reader = new PostgreSqlSchemaCatalogReader();
            var tables = await reader.ListTablesAsync(conn, CancellationToken.None);
            var actual = await reader.ReadAsync(conn, tables, CancellationToken.None);

            GovernanceSchemaTestSupport.AssertSnapshotEquals("postgresql", actual);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_94_08c_ShapeTableName_IsParameterBound()
    {
        // Ported from the former SQLite catalog reader test (WP-SIMPL-11).
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await ExecAsync(db.ConnectionString, "CREATE TABLE t1 (id TEXT PRIMARY KEY, v TEXT); CREATE UNIQUE INDEX ux_t1_v ON t1 (v);");
        var hostile = new[] { "x'; DROP TABLE t1;--", "t1' OR '1'='1", "t1;", "\"t1\"", "t1" };
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var shape = await new PostgreSqlSchemaCatalogReader().ReadAsync(conn, hostile, CancellationToken.None);

        shape.Tables.Select(t => t.Name).ShouldBe(["t1"]);
        shape.Tables[0].PrimaryKey.ShouldBe(["id"]);
        shape.Tables[0].Indexes.ShouldContain(i => i.Columns.SequenceEqual(new[] { "v" }));
        (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM pg_class WHERE relname = 't1'")).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SchemaCatalogReader_Command_EnlistsTheActiveTransaction()
    {
        // Ported from the former SQLite catalog reader test (WP-SIMPL-11): PostgreSQL requires every command to enlist the transaction.
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        using var withTx = SchemaCatalogReaderSupport.Command(new CatalogSession(conn, tx), "SELECT 1");

        withTx.Transaction.ShouldBe(tx);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Verifier_InsideATransaction_ReadsThroughIt()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var verifier = new GovernanceSchemaShapeVerifier("postgresql", new PostgreSqlSchemaCatalogReader());

        var result = await verifier.VerifyAsync(conn, CancellationToken.None, tx);

        result.Deviations.ShouldNotBeEmpty(); // empty database: every table missing, read without error inside the transaction
    }

    private static async Task<SchemaShapeResult> VerifyAfterBaselineAsync(params string[] statements)
    {
        // Ported from the former SQLite verifier tests (WP-SIMPL-11): the baseline DDL (v1.5.0 shape) is applied directly, then altered.
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        PostgreSqlBaselineSchema.Apply(conn, null);
        foreach (var statement in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }

        return await new GovernanceSchemaShapeVerifier("postgresql", new PostgreSqlSchemaCatalogReader()).VerifyAsync(conn, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Verify_WhenConsumerAppTableMissing_FailsClosed()
    {
        // SEC-286-2 / AC-3: a missing CONSUMER_APP_* table is a not-ready deviation that names the table.
        var result = await VerifyAfterBaselineAsync("DROP TABLE consumer_app_actor_daily");

        var deviation = result.Deviations.ShouldHaveSingleItem();
        deviation.Kind.ShouldBe(SchemaDeviationKind.MissingTable);
        deviation.Table.ShouldBe("consumer_app_actor_daily");
        GovernanceSchemaShapeComparer.FormatMessage("PostgreSQL", "db", [1], [], result.Deviations).ShouldContain("consumer_app_actor_daily");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Verify_WhenConsumerAppColumnMissing_FailsClosed()
    {
        var result = await VerifyAfterBaselineAsync("ALTER TABLE consumer_app_daily DROP COLUMN rej_other");

        var deviation = result.Deviations.ShouldHaveSingleItem();
        deviation.Kind.ShouldBe(SchemaDeviationKind.MissingColumn);
        deviation.Table.ShouldBe("consumer_app_daily");
        deviation.Name.ShouldBe("rej_other");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Verify_OnExistingBaselineWithoutConsumerTables_IsNotReady()
    {
        var result = await VerifyAfterBaselineAsync(
            "DROP TABLE consumer_app_daily", "DROP TABLE consumer_app_actor_daily", "DROP TABLE consumer_app_queryname_daily");

        result.Deviations.Select(d => d.Table).ShouldBe(
            ["consumer_app_actor_daily", "consumer_app_daily", "consumer_app_queryname_daily"], ignoreOrder: true);
        result.Deviations.ShouldAllBe(d => d.Kind == SchemaDeviationKind.MissingTable);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Verify_AfterBaselineOnly_HasNoConsumerAppDeviation()
    {
        (await VerifyAfterBaselineAsync()).Deviations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Adoption_PartialBaselineWithoutHistory_IsCompleted()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = db.ConnectionString, SeedDemoData = false }
        });
        using (var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options))
        {
            await new PostgreSqlGovernanceMigrationRunner(repository, options) { StepVersionCapOverride = 1 }.RunAsync();
        }

        await ExecAsync(db.ConnectionString, "DROP TABLE autheris_schema_migrations");

        using (var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options))
        {
            await new PostgreSqlGovernanceMigrationRunner(repository, options) { StepVersionCapOverride = 1 }.RunAsync();
        }

        (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 1")).ShouldBe(1);
    }

    [Fact]
    public async Task AppliedBaseline_PassesApplyAndVerify_AndRunsAreIdempotent()
    {

        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_shared.Db.ConnectionString, GovernanceMigrationMode.Apply);
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_shared.Db.ConnectionString, GovernanceMigrationMode.Apply);
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_shared.Db.ConnectionString, GovernanceMigrationMode.Verify);

        (await ScalarAsync(_shared.Db.ConnectionString, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(9, "baseline and steps 0002 to 0009");
    }

    [Fact]
    public async Task Verify_OnUnmigratedDatabase_FailsAndCreatesNothing()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();

        await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify));

        (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'")).ShouldBe(0);
    }

    [Fact]
    public async Task PreCreatedTableWithoutColumn_Apply_ReportsDriftAndRecordsNoVersion()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await ExecAsync(
            db.ConnectionString,
            "CREATE TABLE CONSENT_COLUMN_RULES (id TEXT PRIMARY KEY, consent_id TEXT NOT NULL, table_column_id TEXT NOT NULL, column_name TEXT NOT NULL, access_level TEXT NOT NULL)");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply));

        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.MissingColumn && d.Table == "consent_column_rules" && d.Name == "applicable_scopes");
        ex.Message.ShouldContain("PostgreSQL database");
        // I388-SC-04: no DDL before the adoption pre-check accepted the schema, so not even the history table exists.
        (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM pg_class WHERE relname = 'autheris_schema_migrations'")).ShouldBe(0);

        // Fails on every start, not only the first.
        await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply));
    }

    [Fact]
    public async Task ShadowTableInOtherSchema_DoesNotSatisfyShape()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);

        // A complete copy of the table in another schema must not satisfy the check once the real one is gone.
        await ExecAsync(db.ConnectionString, "CREATE SCHEMA shadow; CREATE TABLE shadow.table_indexes (LIKE public.table_indexes INCLUDING ALL); DROP TABLE public.table_index_columns; DROP TABLE public.table_indexes;");
        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify));
        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.MissingTable && d.Table == "table_indexes");

        // And a drifted shadow earlier in the search_path hides the intact table, exactly as the runtime would resolve it.
        await ExecAsync(db.ConnectionString, "DROP SCHEMA shadow CASCADE;");
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            PostgreSqlBaselineSchema.Apply(conn, null); // restores the dropped tables (idempotent DDL)
        }

        await ExecAsync(db.ConnectionString, "CREATE SCHEMA shadow; CREATE TABLE shadow.consent_column_rules (id TEXT PRIMARY KEY); "
            + "CREATE TABLE shadow.autheris_schema_migrations (LIKE public.autheris_schema_migrations INCLUDING ALL); "
            + "INSERT INTO shadow.autheris_schema_migrations SELECT * FROM public.autheris_schema_migrations;");
        var shadowed = new NpgsqlConnectionStringBuilder(db.ConnectionString) { SearchPath = "shadow,public" }.ConnectionString;
        var hidden = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(shadowed, GovernanceMigrationMode.Verify));
        hidden.Deviations.ShouldContain(d => d.Table == "consent_column_rules" && d.Kind == SchemaDeviationKind.MissingColumn);
    }

    [Fact]
    public async Task DriftMessage_ContainsNoConnectionDetails()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
        await ExecAsync(db.ConnectionString, "ALTER TABLE consent_column_rules DROP COLUMN applicable_scopes");
        var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString);

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify));

        var header = ex.Message[..ex.Message.LastIndexOf('\n')];
        header.ShouldContain($"database '{builder.Database}'");
        foreach (var forbidden in new[] { builder.Host!, builder.Username!, builder.Password!, "Password", "Host=", "Server=", "Username" })
        {
            if (!string.IsNullOrEmpty(forbidden))
            {
                header.ShouldNotContain(forbidden, Case.Sensitive);
            }
        }
    }
}
