namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DatasetDefinitionSourceTypeSelfHealTests : IClassFixture<DatasetDefinitionSourceTypeSelfHealTests.HostFixture>
{
    private readonly HostFixture _fixture;

    public DatasetDefinitionSourceTypeSelfHealTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        public string GovernanceConnectionString { get; } =
            GovernanceTestDatabase.Create("DatasetDefinitionSourceTypeSelfHealTests");

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceConnectionString,
                ["Gateway:Tenancy:DefaultTenantId"] = "default",
                ["Gateway:DataSources:Connections:kostentransparenz_dbx:Provider"] = "Databricks",
                ["Gateway:DataSources:Connections:kostentransparenz_dbx:Databricks:Host"] = "adb-123.azuredatabricks.net",
                ["Gateway:DataSources:Connections:kostentransparenz_dbx:Databricks:WarehouseId"] = "wh-selfheal",
                ["Gateway:DataSources:Connections:kostentransparenz_dbx:Databricks:Auth:Mode"] = "Pat",
                ["Gateway:DataSources:Connections:kostentransparenz_dbx:Databricks:Auth:PatRef"] = "kv://dbx-pat"
            });
        }
    }

    private HttpClient CreateAdminClient()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SELFHEAL");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin,GOVERNANCE_ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "default");
        return client;
    }

    [Fact]
    public async Task PutDatasetDefinition_RewritesPoisonedLegacySourceTypeInPostgreSql()
    {
        using var client = CreateAdminClient();
        var tableId = new TableIdentifier("kostentransparenz_dbx", "controlling", "dim_date");
        var tableGuid = Guid.NewGuid();
        var colGuid = Guid.NewGuid();

        using (var seedScope = _fixture.Services.CreateScope())
        {
            var repo = seedScope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = tableId,
                Table = new Table
                {
                    Id = tableGuid,
                    SourceName = "kostentransparenz_dbx",
                    SchemaName = "controlling",
                    TableName = "dim_date",
                    DisplayName = "dim_date",
                    SourceType = "Databricks",
                    IsActive = true,
                    DataSourceType = DataSourceType.Sql
                },
                Columns = [new TableColumn { Id = colGuid, TableId = tableGuid, ColumnName = "Year", DataType = "int" }]
            });
        }

        // Simulate a legacy pre-#1116 row whose TABLES.source_type holds the raw connection name.
        GovernanceTestDatabase.ExecuteRaw(
            _fixture.GovernanceConnectionString,
            "UPDATE TABLES SET source_type = @legacy WHERE id = @id",
            parameters: [("@legacy", "kostentransparenz_dbx"), ("@id", tableGuid.ToString())]);
        GovernanceTestDatabase.ExecuteRaw(
            _fixture.GovernanceConnectionString,
            "UPDATE POLICY_EPOCHS SET epoch = epoch + 1 WHERE table_id = @id",
            parameters: [("@id", tableGuid.ToString())]);

        var payload = new
        {
            schemaVersion = 1,
            dataSource = "kostentransparenz_dbx",
            kind = "table",
            columns = new[]
            {
                new { name = "Year", nativeType = "int" }
            }
        };

        var response = await client.PutAsJsonAsync(
            "/api/v1/catalog/datasets/kostentransparenz_dbx.controlling.dim_date/definition",
            payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var persistedSourceType = GovernanceTestDatabase.ScalarRaw(
            _fixture.GovernanceConnectionString,
            "SELECT source_type FROM TABLES WHERE id = @id",
            ("@id", tableGuid.ToString())) as string;
        persistedSourceType.ShouldBe("Databricks");

        using var verifyScope = _fixture.Services.CreateScope();
        var verifyRepo = verifyScope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var loaded = await verifyRepo.GetTableMetadataAsync(tableId);
        loaded.ShouldNotBeNull();
        loaded.Table.SourceType.ShouldBe("Databricks");
        loaded.Table.Dialect.ShouldBe(DatabaseDialect.Databricks);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HealConfiguredSqlTablesAsync_InPostgreSql_RepairsConfiguredAndLeavesUnconfiguredUntouched()
    {
        using var _ = _fixture.CreateClient();
        var configuredGuid = Guid.NewGuid();
        var unconfiguredGuid = Guid.NewGuid();
        var configuredId = new TableIdentifier("kostentransparenz_dbx", "controlling", "legacy_fact");
        var unconfiguredId = new TableIdentifier("unconfigured_legacy", "controlling", "orphan_tbl");

        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var writeScopeFactory = scope.ServiceProvider.GetRequiredService<ICatalogWriteScopeFactory>();

        await repo.UpsertTableMetadataBatchAsync([
            new TableMetadata
            {
                Identifier = configuredId,
                Table = new Table
                {
                    Id = configuredGuid,
                    SourceName = "kostentransparenz_dbx",
                    SchemaName = "controlling",
                    TableName = "legacy_fact",
                    DisplayName = "legacy_fact",
                    SourceType = "Databricks",
                    IsActive = true,
                    DataSourceType = DataSourceType.Sql
                },
                Columns = [new TableColumn { Id = Guid.NewGuid(), TableId = configuredGuid, ColumnName = "id", DataType = "int" }]
            },
            new TableMetadata
            {
                Identifier = unconfiguredId,
                Table = new Table
                {
                    Id = unconfiguredGuid,
                    SourceName = "unconfigured_legacy",
                    SchemaName = "controlling",
                    TableName = "orphan_tbl",
                    DisplayName = "orphan_tbl",
                    SourceType = "PostgreSql",
                    IsActive = true,
                    DataSourceType = DataSourceType.Sql
                },
                Columns = [new TableColumn { Id = Guid.NewGuid(), TableId = unconfiguredGuid, ColumnName = "id", DataType = "int" }]
            }
        ]);

        GovernanceTestDatabase.ExecuteRaw(
            _fixture.GovernanceConnectionString,
            "UPDATE TABLES SET source_type = @legacy WHERE id = @id",
            parameters: [("@legacy", "kostentransparenz_dbx"), ("@id", configuredGuid.ToString())]);
        GovernanceTestDatabase.ExecuteRaw(
            _fixture.GovernanceConnectionString,
            "UPDATE TABLES SET source_type = @legacy WHERE id = @id",
            parameters: [("@legacy", "unconfigured_legacy"), ("@id", unconfiguredGuid.ToString())]);
        GovernanceTestDatabase.ExecuteRaw(
            _fixture.GovernanceConnectionString,
            "UPDATE POLICY_EPOCHS SET epoch = epoch + 1 WHERE table_id IN (@id1, @id2)",
            parameters: [("@id1", configuredGuid.ToString()), ("@id2", unconfiguredGuid.ToString())]);

        var healedCount = await CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(
            repo,
            options,
            writeScopeFactory,
            logger: null,
            CancellationToken.None);

        healedCount.ShouldBe(1);

        var repaired = GovernanceTestDatabase.ScalarRaw(
            _fixture.GovernanceConnectionString,
            "SELECT source_type FROM TABLES WHERE id = @id",
            ("@id", configuredGuid.ToString())) as string;
        repaired.ShouldBe("Databricks");

        var untouched = GovernanceTestDatabase.ScalarRaw(
            _fixture.GovernanceConnectionString,
            "SELECT source_type FROM TABLES WHERE id = @id",
            ("@id", unconfiguredGuid.ToString())) as string;
        untouched.ShouldBe("unconfigured_legacy");
    }
}
