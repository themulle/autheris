using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Row scope lookup of procedure results: governed read connection, key uniqueness verified in the database, dialect
/// specific SQL (SQL Server, PostgreSQL, SQLite). SQLite runs for real; the other dialects are checked on the SQL text
/// (PostgreSQL also by a Testcontainers contract test).
/// </summary>
public sealed class SqlProcedureRowScopeResolverTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"autheris-rowscope-{Guid.NewGuid():N}.db");

    public SqlProcedureRowScopeResolverTests()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE crane (serial_number TEXT PRIMARY KEY, client_id INTEGER NOT NULL, is_delivered INTEGER, tenant_id TEXT NOT NULL);
CREATE UNIQUE INDEX ux_crane_partial ON crane (client_id) WHERE is_delivered IS NULL;
CREATE TABLE crane_state (id INTEGER PRIMARY KEY, client_id INTEGER NOT NULL, code TEXT NOT NULL, tenant_id TEXT NOT NULL);
CREATE UNIQUE INDEX ux_state_code ON crane_state (client_id, code);
CREATE UNIQUE INDEX ux_state_expr ON crane_state (id, lower(code));
CREATE TABLE client (client_id INTEGER NOT NULL, region TEXT NOT NULL);
INSERT INTO crane VALUES ('100', 1, 1, 'tenant-a'), ('200', 2, NULL, 'tenant-a'), ('300', 3, NULL, 'tenant-b');
INSERT INTO crane_state VALUES (1, 1, 'A', 'tenant-a'), (2, 1, 'B', 'tenant-a'), (3, 2, 'A', 'tenant-a');
INSERT INTO client VALUES (1, 'CH'), (2, 'DE'), (1, 'AT');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private SqlProcedureRowScopeResolver Resolver(string connectionName = "erp", string provider = "Sqlite") => new(
        new SqlConnectionFactory(),
        Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    [connectionName] = new() { Provider = provider, ConnectionString = $"Data Source={_dbPath}" }
                }
            }
        }));

    private static TableMetadata Table(string name, string sourceType = "Sqlite", params string[] columns) => new()
    {
        Identifier = new TableIdentifier("erp", "main", name),
        Table = new Table { SourceName = "erp", SchemaName = "main", TableName = name, SourceType = sourceType, DataSourceType = DataSourceType.Sql },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static ProcedureDefinition Definition() =>
        ProcedureDefinitionParser.Parse("-- @name p\n-- @procedure tem.usp_P\n-- @result-table main.crane\n-- @row-scope-key serial_number", "x", false, 60)
        with { DataSource = "erp" };

    private static TableAccessDecision Decision(TableMetadata table, string? filter) =>
        TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: filter, hasUnconstrainedColumnAllow: true);

    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", null);

    [Fact]
    public async Task PrimaryKey_RowFilterAndTenant_AreAppliedInTheDatabase()
    {
        var table = Table("crane", columns: ["serial_number", "client_id", "is_delivered", "tenant_id"]);

        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "\"autheris_target\".\"is_delivered\" IS NULL"), ["serial_number"],
            [["100"], ["200"], ["300"], ["999"]], Caller, CancellationToken.None);

        // 100 is delivered (filter), 300 belongs to another tenant, 999 does not exist.
        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! }, ignoreOrder: true);
    }

    [Fact]
    public async Task CompositeUniqueIndex_IsAccepted()
    {
        var table = Table("crane_state", columns: ["id", "client_id", "code", "tenant_id"]);

        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "\"autheris_target\".\"code\" = 'A'"), ["client_id", "code"],
            [[1L, "A"], [1L, "B"], [2L, "A"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize([1L, "A"])!, RowScopeKeys.Normalize([2L, "A"])! }, ignoreOrder: true);
    }

    [Fact]
    public async Task CorrelatedRowFilter_BindsToTheReservedAlias()
    {
        var table = Table("crane", columns: ["serial_number", "client_id", "is_delivered", "tenant_id"]);
        string correlated = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("erp", "main", "client"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "client_id",
            PrimaryKeyColumn = "client_id",
            SubqueryFilterPredicateJson = "{\"c.region\": \"DE\"}"
        }, DatabaseDialect.Sqlite);

        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, correlated), ["serial_number"], [["100"], ["200"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! });
    }

    [Theory]
    [InlineData("crane", "client_id")]       // only a partial unique index -> not unique
    [InlineData("crane_state", "client_id")] // part of a composite unique index -> not unique
    [InlineData("client", "client_id")]      // no index at all, duplicates exist
    [InlineData("crane_state", "code")]      // only inside an expression index
    public async Task NonUniqueKey_IsRejected(string tableName, string key)
    {
        var table = Table(tableName, columns: [key, "tenant_id"]);

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "1 = 1"), [key], [[1L]], Caller, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogDialectDifferentFromConnection_IsRejected()
    {
        var table = Table("crane", sourceType: "SqlServer", columns: ["serial_number", "tenant_id"]);

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "[autheris_target].[is_delivered] IS NULL"), ["serial_number"], [["200"]], Caller, CancellationToken.None));
    }

    [Fact]
    public async Task MissingGovernedReadConnection_IsRejected()
    {
        var table = Table("crane", columns: ["serial_number", "tenant_id"]);

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver(connectionName: "procedures-login").GetAllowedKeysAsync(
            Definition(), table, Decision(table, null), ["serial_number"], [["200"]], Caller, CancellationToken.None));
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "SELECT [serial_number] FROM [main].[crane] AS [autheris_target] WHERE [tenant_id] = @rs_tenant AND (f) AND (([serial_number] = @rs_0_0) OR ([serial_number] = @rs_1_0))")]
    [InlineData(DatabaseDialect.PostgreSql, "SELECT \"serial_number\" FROM \"main\".\"crane\" AS \"autheris_target\" WHERE \"tenant_id\" = @rs_tenant AND (f) AND ((\"serial_number\" = @rs_0_0) OR (\"serial_number\" = @rs_1_0))")]
    [InlineData(DatabaseDialect.Sqlite, "SELECT \"serial_number\" FROM \"crane\" AS \"autheris_target\" WHERE \"tenant_id\" = @rs_tenant AND (f) AND ((\"serial_number\" = @rs_0_0) OR (\"serial_number\" = @rs_1_0))")]
    public void KeyQuery_IsQuotedPerDialect(DatabaseDialect dialect, string expected)
    {
        SqlProcedureRowScopeResolver.BuildKeyQuery(dialect, Table("crane"), ["serial_number"], "tenant_id", "f", 2).ShouldBe(expected);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "[main].[crane]", "sys.indexes")]
    [InlineData(DatabaseDialect.PostgreSql, "\"main\".\"crane\"", "pg_index")]
    [InlineData(DatabaseDialect.Sqlite, "crane", "pragma_index_list")]
    public void UniqueIndexQuery_UsesTheDialectCatalog(DatabaseDialect dialect, string tableArgument, string catalog)
    {
        SqlProcedureRowScopeResolver.UniqueIndexTableArgument(dialect, new TableIdentifier("erp", "main", "crane")).ShouldBe(tableArgument);
        SqlProcedureRowScopeResolver.BuildUniqueIndexQuery(dialect).ShouldContain(catalog);
    }

    [Fact]
    public void UniqueIndexMatch_ComparesColumnSetsPerDialect()
    {
        var indexes = new List<(long, string)> { (1, "Client_Id"), (1, "code"), (2, "id") };

        SqlProcedureRowScopeResolver.HasUniqueIndexOn(DatabaseDialect.SqlServer, indexes, ["code", "client_id"]).ShouldBeTrue();
        SqlProcedureRowScopeResolver.HasUniqueIndexOn(DatabaseDialect.PostgreSql, indexes, ["code", "client_id"]).ShouldBeFalse(); // case-sensitive
        SqlProcedureRowScopeResolver.HasUniqueIndexOn(DatabaseDialect.Sqlite, indexes, ["client_id"]).ShouldBeFalse();              // subset only
        SqlProcedureRowScopeResolver.HasUniqueIndexOn(DatabaseDialect.Sqlite, indexes, ["ID"]).ShouldBeTrue();
    }

    [Fact]
    public void ParameterBudget_RespectsTheDialectLimits()
    {
        SqlProcedureRowScopeResolver.MaxParameters(DatabaseDialect.SqlServer).ShouldBeLessThan(2100);
        SqlProcedureRowScopeResolver.MaxParameters(DatabaseDialect.Sqlite).ShouldBeLessThan(999);
        SqlProcedureRowScopeResolver.MaxParameters(DatabaseDialect.PostgreSql).ShouldBeLessThan(65535);
    }
}
