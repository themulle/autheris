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
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// Row scope lookup of procedure results against a real PostgreSQL: pg_index uniqueness check, read-only transaction with
/// transaction-local security settings, quoting and correlated filters. Needs Docker; without it the tests return early.
/// </summary>
public sealed class PostgreSqlRowScopeContractTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
            return;
        }

        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
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
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    private SqlProcedureRowScopeResolver Resolver() => new(
        new SqlConnectionFactory(),
        Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["erp"] = new() { Provider = "PostgreSql", ConnectionString = _container!.GetConnectionString() }
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
        ProcedureDefinitionParser.Parse("-- @name p\n-- @procedure md.usp_P\n-- @result-table md.crane\n-- @row-scope-key serial_number", "x", false, 60)
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
    public async Task PartialUniqueIndex_IsNotAcceptedAsKey()
    {
        if (!_available) return;

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver().GetAllowedKeysAsync(
            Definition(), Crane(), Decision("1 = 1"), ["client_id"], [[2]], Caller, CancellationToken.None));
    }
}
