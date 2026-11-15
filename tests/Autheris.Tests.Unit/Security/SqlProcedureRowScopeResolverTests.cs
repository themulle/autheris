using System;
using System.Collections.Generic;
using System.Data.Common;
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
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Procedures;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Pure unit tests for <see cref="SqlProcedureRowScopeResolver"/>: governed read connection and dialect checks,
/// key-query quoting per dialect, and parameter budgets. Database execution tests run against PostgreSQL in
/// <see cref="Autheris.Tests.Integration.Security.SqlProcedureRowScopeResolverIntegrationTests"/>.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqlProcedureRowScopeResolverTests
{
    private static SqlProcedureRowScopeResolver Resolver(string connectionName = "erp", string provider = "PostgreSql") => new(
        new InMemoryConnectionFactory(),
        Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    [connectionName] = new() { Provider = provider, ConnectionString = "Host=unused;Database=rowscope" }
                }
            }
        }));

    private static TableMetadata Table(string name, string sourceType = "PostgreSql", params string[] columns) => new()
    {
        Identifier = new TableIdentifier("erp", "main", name),
        Table = new Table { SourceName = "erp", SchemaName = "main", TableName = name, SourceType = sourceType, DataSourceType = DataSourceType.Sql },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static ProcedureDefinition Definition() =>
        TestProcedureYaml.Definition("name: p\nprocedure: tem.usp_P\nresult_table: main.crane\nrow_scope_key: serial_number", "x", false, 60)
        with { DataSource = "erp" };

    private static TableAccessDecision Decision(TableMetadata table, string? filter) =>
        TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: filter, hasUnconstrainedColumnAllow: true);

    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", null);

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
    [InlineData(DatabaseDialect.SqlServer, "SELECT [serial_number] FROM [main].[crane] AS [autheris_target] WHERE (([serial_number] = @rs_0_0) OR ([serial_number] = @rs_1_0)) GROUP BY [serial_number] HAVING COUNT(*) = SUM(CASE WHEN [tenant_id] = @rs_tenant AND (f) THEN 1 ELSE 0 END)")]
    [InlineData(DatabaseDialect.PostgreSql, "SELECT \"serial_number\" FROM \"main\".\"crane\" AS \"autheris_target\" WHERE ((\"serial_number\" = @rs_0_0) OR (\"serial_number\" = @rs_1_0)) GROUP BY \"serial_number\" HAVING COUNT(*) = SUM(CASE WHEN \"tenant_id\" = @rs_tenant AND (f) THEN 1 ELSE 0 END)")]
    public void KeyQuery_IsQuotedPerDialect(DatabaseDialect dialect, string expected)
    {
        SqlProcedureRowScopeResolver.BuildKeyQuery(dialect, Table("crane"), ["serial_number"], "tenant_id", "f", 2).ShouldBe(expected);
    }

    [Fact]
    public void ParameterBudget_RespectsTheDialectLimits()
    {
        SqlProcedureRowScopeResolver.MaxParameters(DatabaseDialect.SqlServer).ShouldBeLessThan(2100);
        SqlProcedureRowScopeResolver.MaxParameters(DatabaseDialect.PostgreSql).ShouldBeLessThan(65535);
    }

    private sealed class InMemoryConnectionFactory : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var conn = new CallbackDbConnection((_, _) => new StubDbDataReader([], []));
            conn.Open();
            return Task.FromResult<DbConnection>(conn);
        }
    }
}
