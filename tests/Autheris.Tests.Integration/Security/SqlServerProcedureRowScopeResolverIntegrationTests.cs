namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
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
using Autheris.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// SQL Server execution tests for <see cref="SqlProcedureRowScopeResolver"/>: primary key, <c>EXISTS</c> row filters
/// without aggregate subquery errors, non-unique key closure, and fail-closed <c>NULL</c> filter evaluation.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqlServerProcedureRowScopeResolverIntegrationTests : IAsyncLifetime
{
    private SqlServerTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await SqlServerTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            _available = true;
        }
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The SQL Server test container");
            return;
        }

        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE dbo.crane (
                serial_number nvarchar(64) NOT NULL PRIMARY KEY,
                client_id bigint NOT NULL,
                is_delivered bigint NULL,
                tenant_id nvarchar(64) NOT NULL
            );
            CREATE TABLE dbo.crane_state (
                id bigint NOT NULL PRIMARY KEY,
                client_id bigint NOT NULL,
                code nvarchar(32) NOT NULL,
                tenant_id nvarchar(64) NOT NULL
            );
            CREATE TABLE dbo.client (
                client_id bigint NOT NULL,
                region nvarchar(32) NOT NULL
            );
            CREATE TABLE dbo.allowed_codes (
                code nvarchar(32) NOT NULL
            );
            CREATE TABLE dbo.crane_nullable (
                id bigint NOT NULL PRIMARY KEY,
                group_id bigint NOT NULL,
                flag bigint NULL,
                tenant_id nvarchar(64) NOT NULL
            );
            INSERT INTO dbo.crane (serial_number, client_id, is_delivered, tenant_id) VALUES
                (N'100', 1, 1, N'tenant-a'),
                (N'200', 2, NULL, N'tenant-a'),
                (N'300', 3, NULL, N'tenant-b');
            INSERT INTO dbo.crane_state (id, client_id, code, tenant_id) VALUES
                (1, 1, N'A', N'tenant-a'),
                (2, 1, N'B', N'tenant-a'),
                (3, 2, N'A', N'tenant-a');
            INSERT INTO dbo.client (client_id, region) VALUES
                (1, N'CH'),
                (2, N'DE'),
                (1, N'AT');
            INSERT INTO dbo.allowed_codes (code) VALUES (N'A');
            INSERT INTO dbo.crane_nullable (id, group_id, flag, tenant_id) VALUES
                (1, 10, 1, N'tenant-a'),
                (2, 10, NULL, N'tenant-a'),
                (3, 20, 1, N'tenant-a'),
                (4, 30, NULL, N'tenant-a');
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
                    ["erp"] = new() { Provider = "SqlServer", ConnectionString = _db!.ConnectionString, DefaultSchema = "dbo" }
                }
            }
        }));

    private static TableMetadata Table(string name, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("erp", "dbo", name),
        Table = new Table { SourceName = "erp", SchemaName = "dbo", TableName = name, SourceType = "SqlServer", DataSourceType = DataSourceType.Sql },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static ProcedureDefinition Definition() =>
        ProcedureDefinitionParser.ParseYaml("name: p\nprocedure: dbo.usp_P\nresult_table: dbo.crane\nrow_scope_key: serial_number", "x", false, 60)
        with { DataSource = "erp" };

    private static TableAccessDecision Decision(TableMetadata table, string? filter) =>
        TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: filter, hasUnconstrainedColumnAllow: true);

    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", null);

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task PrimaryKey_RowFilterAndTenant_AreAppliedInTheDatabase()
    {
        if (!_available) return;

        var table = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, "[autheris_target].[is_delivered] IS NULL"), ["serial_number"],
            [["100"], ["200"], ["300"], ["999"]], Caller, CancellationToken.None);

        // 100 is delivered (filter), 300 belongs to another tenant, 999 does not exist.
        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! }, ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ExistsRowFilter_IsEvaluated_NotRejected()
    {
        if (!_available) return;

        var table = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        const string existsFilter = "EXISTS (SELECT 1 FROM dbo.client r WHERE r.client_id = autheris_target.client_id AND r.region = 'DE')";
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), table, Decision(table, existsFilter), ["serial_number"],
            [["100"], ["200"], ["300"]], Caller, CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["200"])! });
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ExistsRowFilter_NonUniqueKey_MixedRowsRejected()
    {
        if (!_available) return;

        var state = Table("crane_state", "id", "client_id", "code", "tenant_id");
        const string existsFilter = "EXISTS (SELECT 1 FROM dbo.allowed_codes a WHERE a.code = autheris_target.code)";
        var allowed = await Resolver().GetAllowedKeysAsync(
            Definition(), state, Decision(state, existsFilter),
            ["client_id"], [[1L], [2L]], Caller, CancellationToken.None);

        // client_id = 1 has code 'A' (allowed) and code 'B' (denied), so key closure rejects client_id = 1 and keeps client_id = 2.
        allowed.ShouldBe(new[] { RowScopeKeys.Normalize([2L])! });
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task RowFilterEvaluatingToNull_KeyRejected()
    {
        if (!_available) return;

        var crane = Table("crane", "serial_number", "client_id", "is_delivered", "tenant_id");
        var craneAllowed = await Resolver().GetAllowedKeysAsync(
            Definition(), crane, Decision(crane, "[autheris_target].[is_delivered] = 1"), ["serial_number"],
            [["100"], ["200"]], Caller, CancellationToken.None);

        // '200' has is_delivered IS NULL so the comparison evaluates to UNKNOWN and is rejected fail-closed.
        craneAllowed.ShouldBe(new[] { RowScopeKeys.Normalize(["100"])! });

        var nullable = Table("crane_nullable", "id", "group_id", "flag", "tenant_id");
        var groupAllowed = await Resolver().GetAllowedKeysAsync(
            Definition(), nullable, Decision(nullable, "[autheris_target].[flag] = 1"), ["group_id"],
            [[10L], [20L], [30L]], Caller, CancellationToken.None);

        // group_id 10 has one passing row and one NULL (UNKNOWN) row; key closure rejects group 10 and keeps only group 20.
        groupAllowed.ShouldBe(new[] { RowScopeKeys.Normalize([20L])! });
    }
}
