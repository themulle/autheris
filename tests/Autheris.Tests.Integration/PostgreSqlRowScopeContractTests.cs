namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Row scope lookup of procedure results against a real PostgreSQL: pg_index uniqueness check, read-only transaction with
/// transaction-local security settings, quoting and correlated filters. Needs Docker; without it the tests return early.
/// </summary>
public sealed class PostgreSqlRowScopeContractTests : IAsyncLifetime
{
    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            _available = true;
        }
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
            return;
        }

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE SCHEMA md;
CREATE TABLE md.crane (serial_number text PRIMARY KEY, client_id integer NOT NULL, is_delivered boolean, tenant_id text NOT NULL);
CREATE UNIQUE INDEX ux_crane_partial ON md.crane (client_id) WHERE is_delivered IS NULL;
CREATE TABLE md.client (client_id integer NOT NULL, region text NOT NULL);
CREATE SEQUENCE md.side_effect;
INSERT INTO md.crane VALUES ('100', 1, true, 'tenant-a'), ('200', 2, NULL, 'tenant-a'), ('300', 3, NULL, 'tenant-b');
INSERT INTO md.client VALUES (1, 'CH'), (2, 'DE'), (1, 'AT');";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private SqlProcedureRowScopeResolver Resolver() => new(
        new SqlConnectionFactory(new DevelopmentEnvironment()),
        Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["erp"] = new() { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, DefaultSchema = "public" }
                }
            }
        }));

    private static TableMetadata Crane() => new()
    {
        Identifier = new TableIdentifier("erp", "md", "crane"),
        Table = new Table { SourceName = "erp", SchemaName = "md", TableName = "crane", SourceType = "PostgreSQL", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "serial_number" }, new TableColumn { ColumnName = "client_id" }, new TableColumn { ColumnName = "is_delivered" }, new TableColumn { ColumnName = "tenant_id" }]
    };

    private static ProcedureDefinition Definition() =>
        ProcedureDefinitionParser.ParseYaml("name: p\nprocedure: md.usp_P\nresult_table: md.crane\nrow_scope_key: serial_number", "x", false, 60)
        with { DataSource = "erp" };

    private static TableAccessDecision Decision(string? filter) =>
        TableAccessDecision.Allowed(new TableIdentifier("erp", "md", "crane"), new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: filter, hasUnconstrainedColumnAllow: true);

    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", "analytics");

    [Fact]
    public async Task PrimaryKey_FilterAndTenant_AreAppliedInAReadOnlyTransaction()
    {
        if (!_available) return;

        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), Crane(), Decision("\"autheris_target\".\"is_delivered\" IS NULL"), ["serial_number"],
            [["100"], ["200"], ["300"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! });
    }

    [Fact]
    public async Task Lookup_RunsInReadOnlyTransaction_SideEffectsAreRejected()
    {
        if (!_available) return;

        // R-SQL-4: nextval() writes; inside SET TRANSACTION READ ONLY PostgreSQL refuses it.
        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(() => Resolver().GetAllowedKeysAsync(
            Definition(), Crane(), Decision("nextval('md.side_effect') > 0"), ["serial_number"], [["100"]], Caller, CancellationToken.None));

        ex.SqlState.ShouldBe("25006"); // read_only_sql_transaction
    }

    [Fact]
    public async Task CorrelatedFilter_InPostgreSqlDialect_Binds()
    {
        if (!_available) return;

        string correlated = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("erp", "md", "client"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "client_id",
            PrimaryKeyColumn = "client_id",
            SubqueryFilterPredicateJson = "{\"c.region\": \"DE\"}"
        }, DatabaseDialect.PostgreSql);

        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), Crane(), Decision(correlated), ["serial_number"], [["100"], ["200"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! });
    }

    [Fact]
    public async Task Non_unique_key_never_widens_access()
    {
        if (!_available) return;

        await using (var conn = new NpgsqlConnection(_db!.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS md.crane_dup (serial_number text PRIMARY KEY, client_id integer NOT NULL, is_delivered boolean, tenant_id text NOT NULL);
TRUNCATE md.crane_dup;
INSERT INTO md.crane_dup VALUES ('200', 2, NULL, 'tenant-a'), ('201', 2, true, 'tenant-a'), ('300', 3, NULL, 'tenant-a');";
            await cmd.ExecuteNonQueryAsync();
        }

        var dupTable = new TableMetadata
        {
            Identifier = new TableIdentifier("erp", "md", "crane_dup"),
            Table = new Table { SourceName = "erp", SchemaName = "md", TableName = "crane_dup", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns =
            [
                new TableColumn { ColumnName = "serial_number" },
                new TableColumn { ColumnName = "client_id" },
                new TableColumn { ColumnName = "is_delivered" },
                new TableColumn { ColumnName = "tenant_id" }
            ]
        };

        var decision = TableAccessDecision.Allowed(dupTable.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "\"is_delivered\" IS NULL", hasUnconstrainedColumnAllow: true);
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), dupTable, decision, ["client_id"], [[2], [3]], Caller, CancellationToken.None);

        // client_id = 2 has mixed rows (is_delivered NULL and true), so key closure admits only client_id = 3.
        allowed.ShouldBe(new[] { RowScopeKeys.Normalize([3])! });
    }
}
