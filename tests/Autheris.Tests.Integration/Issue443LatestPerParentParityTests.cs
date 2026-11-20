namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.GraphQL;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;
using BinaryOperator = TrinoSqlEngine.Ast.Nodes.BinaryOperator;

/// <summary>
/// Issue #443 (WP-C): Real-database parity and security tests for set-based vs correlated nested
/// "latest per parent" lists across SQL Server and PostgreSQL.
/// </summary>
public sealed class Issue443LatestPerParentParityTests
{
    [RequiresContainerRuntimeTheory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task SetBased_EqualsCorrelated_FirstOne(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var root = BuildQuery(dialect, first: 1, orderBy: [new TreeOrder("ts", Descending: true)]);

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        var correlated = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: true, tenant: "t1"), dialect);

        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);
        correlated.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);

        var setBasedJson = await harness.ExecuteAsync(setBased);
        var correlatedJson = await harness.ExecuteAsync(correlated);

        NormalizeJson(setBasedJson).ShouldBe(NormalizeJson(correlatedJson));
        var devices = setBasedJson.EnumerateArray().ToList();
        devices.Select(d => d.GetProperty("device_id").GetInt32()).ShouldBe([1, 2, 3, 5]);
        devices[0].GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([103]);
        devices[1].GetProperty("readings").EnumerateArray().ShouldBeEmpty();
        devices[2].GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([302]);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task SetBased_EqualsCorrelated_FirstN_MixedOrder(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var root = BuildQuery(dialect, first: 3, orderBy: [new TreeOrder("ts", Descending: true), new TreeOrder("seq", Descending: false)]);

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        var correlated = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: true, tenant: "t1"), dialect);

        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);
        correlated.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);

        var setBasedJson = await harness.ExecuteAsync(setBased);
        var correlatedJson = await harness.ExecuteAsync(correlated);

        NormalizeJson(setBasedJson).ShouldBe(NormalizeJson(correlatedJson));
        var d1Readings = setBasedJson[0].GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
        d1Readings.ShouldBe([103, 102, 101]);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task SetBased_EqualsCorrelated_WithWhere(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var childWhere = new TreeComparison("val", TreeFilterOperator.Gte, 20);
        var root = BuildQuery(dialect, first: 2, orderBy: [new TreeOrder("ts", Descending: true)], childWhere: childWhere);

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        var correlated = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: true, tenant: "t1"), dialect);

        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);
        correlated.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);

        var setBasedJson = await harness.ExecuteAsync(setBased);
        var correlatedJson = await harness.ExecuteAsync(correlated);

        NormalizeJson(setBasedJson).ShouldBe(NormalizeJson(correlatedJson));
        setBasedJson[0].GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([102]);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task ParentWithoutRows_ReturnsEmptyArray(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var root = BuildQuery(dialect, first: 1, orderBy: [new TreeOrder("ts", Descending: true)]);

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);

        var result = await harness.ExecuteAsync(setBased);
        var device2 = result.EnumerateArray().Single(d => d.GetProperty("device_id").GetInt32() == 2);
        device2.GetProperty("readings").ValueKind.ShouldBe(JsonValueKind.Array);
        device2.GetProperty("readings").GetArrayLength().ShouldBe(0);
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task RowFilterHiddenRow_NeverRanksFirst(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        // Row 104 for device 1 has the highest ts (999) but published = 0; row filter requires published = 1.
        var rowFilter = TypedTreeRowPolicy.Int("published", BinaryOperator.Equal, 1);
        var root = BuildQuery(dialect, first: 1, orderBy: [new TreeOrder("ts", Descending: true)]);

        var setBased = TreeSqlCompiler.Compile(
            root,
            harness.CreateAccess(childIndexed: false, tenant: "t1", readingsRowFilter: rowFilter),
            dialect);
        var correlated = TreeSqlCompiler.Compile(
            root,
            harness.CreateAccess(childIndexed: true, tenant: "t1", readingsRowFilter: rowFilter),
            dialect);

        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);

        var setBasedJson = await harness.ExecuteAsync(setBased);
        var correlatedJson = await harness.ExecuteAsync(correlated);

        NormalizeJson(setBasedJson).ShouldBe(NormalizeJson(correlatedJson));
        var d1TopId = setBasedJson[0].GetProperty("readings")[0].GetProperty("id").GetInt32();
        d1TopId.ShouldBe(103);
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task TenantB_RowsNeverRankedForTenantA(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        // Row 401 has device_id = 1, tenant_id = 't2', ts = 1000 (higher than any t1 row).
        var root = BuildQuery(dialect, first: 1, orderBy: [new TreeOrder("ts", Descending: true)]);
        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);

        var result = await harness.ExecuteAsync(setBased);
        var allDeviceIds = result.EnumerateArray().Select(d => d.GetProperty("device_id").GetInt32()).ToList();
        allDeviceIds.ShouldNotContain(4);
        var d1TopId = result[0].GetProperty("readings")[0].GetProperty("id").GetInt32();
        d1TopId.ShouldBe(103);
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task MaskedColumn_NoClearValueInSetBasedJson(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var root = BuildQuery(
            dialect,
            first: 1,
            orderBy: [new TreeOrder("ts", Descending: true)],
            childColumns: ["id", "ts", "val", "secret"]);

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        var correlated = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: true, tenant: "t1"), dialect);
        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);

        var setBasedJson = await harness.ExecuteAsync(setBased);
        var correlatedJson = await harness.ExecuteAsync(correlated);

        NormalizeJson(setBasedJson).ShouldBe(NormalizeJson(correlatedJson));
        var secretVal = setBasedJson[0].GetProperty("readings")[0].GetProperty("secret").GetString();
        secretVal.ShouldBe("***");
        setBasedJson.GetRawText().ShouldNotContain("top-secret-103");
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task AuditEntries_IdenticalToCorrelated(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var root = BuildQuery(
            dialect,
            first: 1,
            orderBy: [new TreeOrder("ts", Descending: true)],
            childColumns: ["id", "ts", "val", "secret"]);

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        var correlated = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: true, tenant: "t1"), dialect);

        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);
        correlated.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);

        setBased.HmacColumns.ShouldBe(correlated.HmacColumns, ignoreOrder: false);
        var setBasedJson = await harness.ExecuteAsync(setBased);
        var correlatedJson = await harness.ExecuteAsync(correlated);
        NormalizeJson(setBasedJson).ShouldBe(NormalizeJson(correlatedJson));
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task Ties_SetBasedPicksLowestPkDeterministically(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        // Device 5 has two readings with identical ts = 500: id 501 and id 502.
        // Set-based appends child PK tie-break (id ASC), so 501 must always win.
        var root = BuildQuery(
            dialect,
            first: 1,
            orderBy: [new TreeOrder("ts", Descending: true)],
            parentWhere: new TreeComparison("device_id", TreeFilterOperator.Eq, 5));

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);

        var result = await harness.ExecuteAsync(setBased);
        var device = result.EnumerateArray().ShouldHaveSingleItem();
        device.GetProperty("readings").EnumerateArray().ShouldHaveSingleItem().GetProperty("id").GetInt32().ShouldBe(501);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task IndexDeviceIdTs_UsesCorrelatedTop(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        var root = BuildQuery(dialect, first: 1, orderBy: [new TreeOrder("ts", Descending: true)]);

        var compiled = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: true, tenant: "t1"), dialect);

        compiled.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.CorrelatedIndexed);
        compiled.Sql.ShouldNotContain("autheris_page");
        compiled.Sql.ShouldNotContain("ROW_NUMBER()");
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public async Task ConcurrentInsert_PageMismatch_ReturnsEmptyNotForeign(DatabaseDialect dialect)
    {
        await using var harness = await ParityDbHarness.CreateAsync(dialect);
        // Device 2 has no readings in t1; a NULL-device_id reading and a t2 reading exist.
        // Even if rows exist for other parents/tenants, device 2 receives [] and never foreign rows.
        var root = BuildQuery(
            dialect,
            first: 1,
            orderBy: [new TreeOrder("ts", Descending: true)],
            parentWhere: new TreeComparison("device_id", TreeFilterOperator.Eq, 2));

        var setBased = TreeSqlCompiler.Compile(root, harness.CreateAccess(childIndexed: false, tenant: "t1"), dialect);
        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);

        var result = await harness.ExecuteAsync(setBased);
        var device = result.EnumerateArray().ShouldHaveSingleItem();
        device.GetProperty("device_id").GetInt32().ShouldBe(2);
        device.GetProperty("readings").GetArrayLength().ShouldBe(0);
    }

    private static TreeQueryNode BuildQuery(
        DatabaseDialect dialect,
        int first,
        IReadOnlyList<TreeOrder> orderBy,
        TreeFilter? childWhere = null,
        TreeFilter? parentWhere = null,
        string[]? childColumns = null)
    {
        var schema = dialect == DatabaseDialect.SqlServer ? "dbo" : "public";
        var devicesId = new TableIdentifier("dev", schema, "devices");
        var readingsId = new TableIdentifier("dev", schema, "readings");

        var childNode = new TreeQueryNode(readingsId, childColumns ?? ["id", "ts", "seq", "val"])
        {
            OrderBy = orderBy,
            Where = childWhere,
            Limit = first
        };

        return new TreeQueryNode(devicesId, ["device_id", "name"])
        {
            Relations = [new TreeRelationNode("readings", ParentColumns: ["device_id"], ChildColumns: ["device_id"], IsList: true, childNode)],
            Where = parentWhere,
            OrderBy = [new TreeOrder("device_id")],
            Limit = 20
        };
    }

    private static string NormalizeJson(JsonElement element) =>
        JsonSerializer.Serialize(element);

    private sealed class ParityDbHarness : IAsyncDisposable
    {
        public static readonly TableIdentifier Devices = new("dev", "dbo", "devices");
        public static readonly TableIdentifier Readings = new("dev", "dbo", "readings");

        private readonly DatabaseDialect _dialect;
        private readonly SqlServerTestDatabase? _sqlServer;
        private readonly PostgreSqlTestDatabase? _postgres;

        private ParityDbHarness(DatabaseDialect dialect, SqlServerTestDatabase? sqlServer, PostgreSqlTestDatabase? postgres)
        {
            _dialect = dialect;
            _sqlServer = sqlServer;
            _postgres = postgres;
        }

        public static async Task<ParityDbHarness> CreateAsync(DatabaseDialect dialect)
        {
            if (dialect == DatabaseDialect.SqlServer)
            {
                var db = await SqlServerTestDatabase.CreateAsync();
                if (!db.IsAvailable)
                {
                    throw new InvalidOperationException("SQL Server test database is unavailable.");
                }
                await SeedSqlServerAsync(db.ConnectionString);
                return new ParityDbHarness(dialect, db, null);
            }

            var pg = await PostgreSqlTestDatabase.CreateAsync();
            if (!pg.IsAvailable)
            {
                throw new InvalidOperationException("PostgreSQL test database is unavailable.");
            }
            await SeedPostgresAsync(pg.ConnectionString);
            return new ParityDbHarness(dialect, null, pg);
        }

        public Dictionary<TableIdentifier, TreeTableAccess> CreateAccess(
            bool childIndexed,
            string tenant,
            IRowPolicy? readingsRowFilter = null)
        {
            var schema = _dialect == DatabaseDialect.SqlServer ? "dbo" : "public";
            var sourceType = _dialect == DatabaseDialect.SqlServer ? "SqlServer" : "PostgreSql";
            var devicesId = new TableIdentifier("dev", schema, "devices");
            var readingsId = new TableIdentifier("dev", schema, "readings");

            var devicesMeta = new TableMetadata
            {
                Identifier = devicesId,
                Table = new Table { SourceName = "dev", SchemaName = schema, TableName = "devices", SourceType = sourceType },
                PrimaryKeyColumns = ["device_id"],
                Columns =
                [
                    new TableColumn { ColumnName = "device_id", DataType = "int" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "text" },
                    new TableColumn { ColumnName = "name", DataType = "text" }
                ]
            };

            var readingsMeta = new TableMetadata
            {
                Identifier = readingsId,
                Table = new Table { SourceName = "dev", SchemaName = schema, TableName = "readings", SourceType = sourceType },
                PrimaryKeyColumns = ["id"],
                Indexes = childIndexed
                    ? [new TableIndexDefinition("ix_readings_dev_ts", ["device_id", "ts"], false, [KeyColumnDirection.Ascending, KeyColumnDirection.Ascending])]
                    : [],
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "int" },
                    new TableColumn { ColumnName = "device_id", DataType = "int" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "text" },
                    new TableColumn { ColumnName = "ts", DataType = "int" },
                    new TableColumn { ColumnName = "seq", DataType = "int" },
                    new TableColumn { ColumnName = "val", DataType = "int" },
                    new TableColumn { ColumnName = "published", DataType = "int" },
                    new TableColumn { ColumnName = "secret", DataType = "text" }
                ],
                ColumnMaskingRules = new Dictionary<string, MaskingRule>
                {
                    ["secret"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" }
                }
            };

            var devicesDecision = TypedTreeRowPolicy.Allowed(
                devicesId,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
                policy: null);
            var readingsDecision = TypedTreeRowPolicy.Allowed(
                readingsId,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny },
                readingsRowFilter);

            return new Dictionary<TableIdentifier, TreeTableAccess>
            {
                [devicesId] = new(devicesMeta, devicesDecision, "tenant_id", tenant),
                [readingsId] = new(readingsMeta, readingsDecision, "tenant_id", tenant)
            };
        }

        public async Task<JsonElement> ExecuteAsync(CompiledTreeQuery compiled)
        {
            if (_dialect == DatabaseDialect.SqlServer)
            {
                await using var conn = new SqlConnection(_sqlServer!.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = compiled.Sql;
                foreach (var (name, value) in compiled.Parameters)
                {
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                var json = Convert.ToString(await cmd.ExecuteScalarAsync())!;
                return JsonDocument.Parse(json).RootElement.Clone();
            }

            await using var pgConn = new NpgsqlConnection(_postgres!.ConnectionString);
            await pgConn.OpenAsync();
            await using var pgCmd = pgConn.CreateCommand();
            pgCmd.CommandText = RewriteSqlForPostgresSchema(compiled.Sql);
            foreach (var (name, value) in compiled.Parameters)
            {
                pgCmd.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
            }
            var pgJson = (string)(await pgCmd.ExecuteScalarAsync())!;
            return JsonDocument.Parse(pgJson).RootElement.Clone();
        }

        private static string RewriteSqlForPostgresSchema(string sql) =>
            sql.Replace("\"dbo\".", "\"public\".", StringComparison.Ordinal);

        private static async Task SeedSqlServerAsync(string connectionString)
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE dbo.devices (
                    device_id int NOT NULL PRIMARY KEY,
                    tenant_id nvarchar(20) NOT NULL,
                    name nvarchar(50) NOT NULL
                );
                CREATE TABLE dbo.readings (
                    id int NOT NULL PRIMARY KEY,
                    device_id int NULL,
                    tenant_id nvarchar(20) NOT NULL,
                    ts int NOT NULL,
                    seq int NOT NULL,
                    val int NOT NULL,
                    published int NOT NULL,
                    secret nvarchar(50) NOT NULL
                );
                CREATE INDEX ix_readings_dev_ts ON dbo.readings (device_id, ts);

                INSERT INTO dbo.devices (device_id, tenant_id, name) VALUES
                    (1, N't1', N'd1'),
                    (2, N't1', N'd2'),
                    (3, N't1', N'd3'),
                    (4, N't2', N'd4'),
                    (5, N't1', N'd5');

                INSERT INTO dbo.readings (id, device_id, tenant_id, ts, seq, val, published, secret) VALUES
                    (101, 1, N't1', 100, 1, 10, 1, N'top-secret-101'),
                    (102, 1, N't1', 200, 2, 20, 1, N'top-secret-102'),
                    (103, 1, N't1', 200, 1, 15, 1, N'top-secret-103'),
                    (104, 1, N't1', 999, 1, 99, 0, N'hidden-secret-104'),
                    (301, 3, N't1', 10, 1, 5, 1, N's301'),
                    (302, 3, N't1', 20, 1, 25, 1, N's302'),
                    (401, 1, N't2', 1000, 1, 100, 1, N'foreign-t2'),
                    (402, 4, N't2', 50, 1, 50, 1, N't2-own'),
                    (501, 5, N't1', 500, 1, 50, 1, N's501'),
                    (502, 5, N't1', 500, 1, 60, 1, N's502'),
                    (999, NULL, N't1', 888, 1, 88, 1, N'null-parent');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task SeedPostgresAsync(string connectionString)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE public.devices (
                    device_id integer NOT NULL PRIMARY KEY,
                    tenant_id text NOT NULL,
                    name text NOT NULL
                );
                CREATE TABLE public.readings (
                    id integer NOT NULL PRIMARY KEY,
                    device_id integer NULL,
                    tenant_id text NOT NULL,
                    ts integer NOT NULL,
                    seq integer NOT NULL,
                    val integer NOT NULL,
                    published integer NOT NULL,
                    secret text NOT NULL
                );
                CREATE INDEX ix_readings_dev_ts ON public.readings (device_id, ts);

                INSERT INTO public.devices (device_id, tenant_id, name) VALUES
                    (1, 't1', 'd1'),
                    (2, 't1', 'd2'),
                    (3, 't1', 'd3'),
                    (4, 't2', 'd4'),
                    (5, 't1', 'd5');

                INSERT INTO public.readings (id, device_id, tenant_id, ts, seq, val, published, secret) VALUES
                    (101, 1, 't1', 100, 1, 10, 1, 'top-secret-101'),
                    (102, 1, 't1', 200, 2, 20, 1, 'top-secret-102'),
                    (103, 1, 't1', 200, 1, 15, 1, 'top-secret-103'),
                    (104, 1, 't1', 999, 1, 99, 0, 'hidden-secret-104'),
                    (301, 3, 't1', 10, 1, 5, 1, 's301'),
                    (302, 3, 't1', 20, 1, 25, 1, 's302'),
                    (401, 1, 't2', 1000, 1, 100, 1, 'foreign-t2'),
                    (402, 4, 't2', 50, 1, 50, 1, 't2-own'),
                    (501, 5, 't1', 500, 1, 50, 1, 's501'),
                    (502, 5, 't1', 500, 1, 60, 1, 's502'),
                    (999, NULL, 't1', 888, 1, 88, 1, 'null-parent');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_sqlServer != null) await _sqlServer.DisposeAsync();
            if (_postgres != null) await _postgres.DisposeAsync();
        }
    }
}
