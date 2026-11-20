namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// PostgreSQL execution tests for <see cref="SqlProcedureRowScopeResolver"/>: primary key, composite unique index,
/// correlated row filter aliasing, and non-unique key closure.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqlProcedureRowScopeResolverIntegrationTests : IAsyncLifetime
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
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS main;
            CREATE TABLE main.crane (serial_number text PRIMARY KEY, client_id bigint NOT NULL, is_delivered bigint, tenant_id text NOT NULL);
            CREATE UNIQUE INDEX ux_crane_partial ON main.crane (client_id) WHERE is_delivered IS NULL;
            CREATE TABLE main.crane_state (id bigint PRIMARY KEY, client_id bigint NOT NULL, code text NOT NULL, tenant_id text NOT NULL);
            CREATE UNIQUE INDEX ux_state_code ON main.crane_state (client_id, code);
            CREATE UNIQUE INDEX ux_state_expr ON main.crane_state (id, lower(code));
            CREATE TABLE main.client (client_id bigint NOT NULL, region text NOT NULL);
            CREATE TABLE main.crane_nullable (id bigint PRIMARY KEY, group_id bigint NOT NULL, flag bigint, tenant_id text NOT NULL);
            INSERT INTO main.crane VALUES ('100', 1, 1, 'tenant-a'), ('200', 2, NULL, 'tenant-a'), ('300', 3, NULL, 'tenant-b');
            INSERT INTO main.crane_state VALUES (1, 1, 'A', 'tenant-a'), (2, 1, 'B', 'tenant-a'), (3, 2, 'A', 'tenant-a');
            INSERT INTO main.client VALUES (1, 'CH'), (2, 'DE'), (1, 'AT');
            INSERT INTO main.crane_nullable VALUES (1, 10, 1, 'tenant-a'), (2, 10, NULL, 'tenant-a'), (3, 20, 1, 'tenant-a'), (4, 30, NULL, 'tenant-a');
            """;
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

    private static TableMetadata Table(string name, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("erp", "main", name),
        Table = new Table { SourceName = "erp", SchemaName = "main", TableName = name, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static ProcedureDefinition Definition() =>
        ProcedureDefinitionParser.ParseYaml("name: p\nprocedure: tem.usp_P\nresult_table: main.crane\nrow_scope_key: serial_number", "x", false, 60)
        with { DataSource = "erp" };

    private static TableAccessDecision Decision(TableMetadata table, string? filter) =>
        TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: filter, hasUnconstrainedColumnAllow: true);

    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", null);

    [RequiresContainerRuntimeFact]
    public async Task PrimaryKey_RowFilterAndTenant_AreAppliedInTheDatabase()
    {
        if (!_available) return;

        var table = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "\"autheris_target\".\"is_delivered\" IS NULL"), ["serial_number"],
            [["100"], ["200"], ["300"], ["999"]], Caller, CancellationToken.None);

        // 100 is delivered (filter), 300 belongs to another tenant, 999 does not exist.
        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! }, ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    public async Task CompositeUniqueIndex_IsAccepted()
    {
        if (!_available) return;

        var table = Table("crane_state", "id", "client_id", "code", "tenant_id");
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "\"autheris_target\".\"code\" = 'A'"), ["client_id", "code"],
            [[1L, "A"], [1L, "B"], [2L, "A"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize([1L, "A"])!, RowScopeKeys.Normalize([2L, "A"])! }, ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    public async Task CorrelatedRowFilter_BindsToTheReservedAlias()
    {
        if (!_available) return;

        var table = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        var vf = new VirtualFilter
        {
            Name = "client_de",
            TenantId = new TenantId("default"),
            Source = "erp",
            KeyColumns = ["c.client_id"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("erp", "main", "client"),
                FromAlias = "c",
                Where = [new FilterCondition("c.region", FilterConditionOperator.Eq, "DE")]
            }
        };
        string correlated = new StructuredFilterSqlBuilder().Build(vf, new FilterBinding { FilterName = "client_de" }, table, DatabaseDialect.PostgreSql);

        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, correlated), ["serial_number"], [["100"], ["200"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! });
    }

    [RequiresContainerRuntimeFact]
    public async Task NonUniqueKey_MixedRowsRejected_ByKeyClosure()
    {
        if (!_available) return;

        var state = Table("crane_state", "id", "client_id", "code", "tenant_id");
        // client_id = 1 has two rows in tenant-a: code 'A' and code 'B'. Under filter code = 'A', only one passes,
        // so key closure excludes client_id = 1 while allowing client_id = 2 (whose only row has code 'A').
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), state, Decision(state, "\"autheris_target\".\"code\" = 'A'"),
            ["client_id"], [[1L], [2L]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize([2L])! });
    }

    [RequiresContainerRuntimeFact]
    public async Task NonUniqueKey_AllRowsAllowed_AcceptedByKeyClosure()
    {
        if (!_available) return;

        var state = Table("crane_state", "id", "client_id", "code", "tenant_id");
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), state, Decision(state, "1 = 1"), ["client_id"], [[1L], [2L]], Caller, CancellationToken.None);

        allowed.ShouldContain(RowScopeKeys.Normalize([1L])!);
        allowed.ShouldContain(RowScopeKeys.Normalize([2L])!);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ExistsRowFilter_IsEvaluated()
    {
        if (!_available) return;

        var table = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        const string existsFilter = "EXISTS (SELECT 1 FROM main.client r WHERE r.client_id = autheris_target.client_id AND r.region = 'DE')";
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, existsFilter), ["serial_number"],
            [["100"], ["200"], ["300"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! });
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task RowFilterEvaluatingToNull_KeyRejected()
    {
        if (!_available) return;

        var crane = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        var craneAllowed = await Resolver().GetAllowedKeysAsync(
            Definition(), crane, Decision(crane, "\"autheris_target\".\"is_delivered\" = 1"), ["serial_number"],
            [["100"], ["200"]], Caller, CancellationToken.None);

        // '200' has is_delivered IS NULL so the comparison evaluates to UNKNOWN and is rejected fail-closed.
        craneAllowed.ShouldBe(new[] { RowScopeKeys.Normalize(["100"])! });

        var nullable = Table("crane_nullable", "id", "group_id", "flag", "tenant_id");
        var groupAllowed = await Resolver().GetAllowedKeysAsync(
            Definition(), nullable, Decision(nullable, "\"autheris_target\".\"flag\" = 1"), ["group_id"],
            [[10L], [20L], [30L]], Caller, CancellationToken.None);

        // group_id 10 has one passing row and one NULL (UNKNOWN) row; key closure rejects group 10 and keeps only group 20.
        groupAllowed.ShouldBe(new[] { RowScopeKeys.Normalize([20L])! });
    }
}
