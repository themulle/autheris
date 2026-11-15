namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Connectors;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>GAP08 / ARC-I-13: <see cref="SqlConnector"/> reads through the governed PostgreSQL pipeline and returns compiler-masked rows.</summary>
[Trait("Category", "Security")]
public sealed class IntegrationGapASqlConnectorIntegrationTests
{
    private const string ConnectorId = "gap-a-sql";
    private const string RawSsn = "123-45-6789";

    private static TableMetadata CreateEmployeesMetadata() => new()
    {
        Identifier = new TableIdentifier("corp", "public", "employees"),
        Table = new Table
        {
            SourceName = ConnectorId,
            SchemaName = "public",
            TableName = "employees",
            DataSourceType = DataSourceType.Sql,
            SourceType = "PostgreSql",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] = new MaskingRule { RuleType = "HMAC_SHA256" }
        }
    };

    [RequiresContainerRuntimeFact]
    public async Task GAP08_SqlConnector_ReadsAreMaskedByTheGovernedPipeline()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var setup = conn.CreateCommand();
            setup.CommandText = $"CREATE TABLE public.employees (id INTEGER, name TEXT, ssn TEXT); INSERT INTO public.employees VALUES (1, 'Alice', '{RawSsn}');";
            await setup.ExecuteNonQueryAsync();
        }

        var env = new PgCompileEnv("public")
            .Table("employees", null, "id:integer", "name", "ssn")
            .Mask("employees", "ssn", PgGovernedSqlService.Redact("ssn", "***"));
        var governed = new PgGovernedSqlService(db.ConnectionString, env);

        var services = new ServiceCollection();
        services.AddScoped<IDataSourceExecutor>(_ => new SqlTableReadExecutor(
            new GovernedTableReadPlanner(), () => governed));
        await using var provider = services.BuildServiceProvider();

        var connector = new SqlConnector(
            connectorId: ConnectorId,
            metadataRepository: new StubTableMetadataRepository(),
            maskingProvider: new StubMaskingProvider(),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var metadata = CreateEmployeesMetadata();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Mask
        });

        var session = new ConnectorSessionContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GAPA")], "Test")),
            Tenant: new TenantId("tenant-gap-a"),
            AccessDecision: decision,
            ProjectedColumns: ["id", "name", "ssn"],
            Arguments: new Dictionary<string, object?>());
        session.Items["TableMetadata"] = metadata;

        var rows = await connector.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), session);
        session.Items["InDbColumnMaskingExecuted"].ShouldBe(true);
        session.Items["RlsPushdownExecuted"].ShouldBe(true);
        rows.Count.ShouldBe(1);
        rows[0]["ssn"].ShouldBe("***");
        rows[0]["name"].ShouldBe("Alice");
    }

    private sealed class StubTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier t, IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> idx,
            IReadOnlyList<KeyColumnDirection>? dir = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class StubMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => "***";
    }
}
