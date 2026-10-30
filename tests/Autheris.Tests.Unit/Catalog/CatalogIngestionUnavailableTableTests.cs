namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Sql.Catalog;
using Autheris.Application.Audit;
using System.Text;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Dbt;
using Autheris.Extensions.OpenMetadata;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #94 (SEC-94-01, SD-3): one corrupt key row loaded directly into the store must not let an ingestion path upsert the table
/// as a new one (which would delete its stored masking rules). Real SQLite store, corrupted rows.
/// </summary>
public sealed class CatalogIngestionUnavailableTableTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repo;

    public CatalogIngestionUnavailableTableTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=unavail_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = Autheris.Tests.Unit.GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
    }

    public void Dispose() => _repo.Dispose();

    private async Task SeedCorruptTableWithMaskingAsync(TableIdentifier id)
    {
        await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceType = "PostgreSQL", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "[REDACTED]" } },
            PrimaryKeyColumns = ["id"],
            ReplaceKeyDefinition = true
        });
        Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 5");
        _repo.InvalidateMetadataCacheForTesting();
        await Should.ThrowAsync<Autheris.Domain.Exceptions.TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(id));
    }

    private void Exec(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void MaskingRuleOfSsnMustSurvive() =>
        Scalar("SELECT COUNT(*) FROM COLUMN_MASKING_RULES m JOIN TABLE_COLUMNS c ON c.id = m.table_column_id WHERE c.column_name = 'ssn' AND c.table_id IN (SELECT id FROM TABLES WHERE is_active = 1 AND table_name <> 'x')")
            .ShouldBeGreaterThanOrEqualTo(1);

    private long TablesNamed(string name) => Scalar($"SELECT COUNT(*) FROM TABLES WHERE table_name = '{name}'");

    [Fact]
    public async Task DataCatalogSync_OnUnavailableTable_DoesNotUpsert_MaskingRulesPreserved()
    {
        var id = new TableIdentifier("crm", "dbo", "orders");
        await SeedCorruptTableWithMaskingAsync(id);
        var client = Substitute.For<IDataCatalogClient>();
        client.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<CatalogTableAsset>
        {
            new() { Identifier = id, Columns = [new() { ColumnName = "id" }, new() { ColumnName = "ssn" }], PrimaryKeyColumns = ["id"] }
        });
        var factory = Substitute.For<IDataCatalogClientFactory>();
        factory.GetActiveClient().Returns(client);
        var sut = new DataCatalogSyncService(factory, _repo, Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions()), Substitute.For<IPhysicalKeyCatalog>(), Substitute.For<IAuditWriter>(), NullLogger<DataCatalogSyncService>.Instance);

        var result = await sut.SyncCatalogAsync(dryRun: false);

        result.Warnings.ShouldContain(w => w.Contains("unavailable"));
        result.SyncedTablesCount.ShouldBe(0);
        MaskingRuleOfSsnMustSurvive();
        Scalar("SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS").ShouldBe(1L, "the corrupt row is left for an administrator");
    }

    [Fact]
    public async Task OpenMetadata_OnUnavailableTable_DoesNotUpsert_MaskingRulesPreserved()
    {
        var id = new TableIdentifier("svc", "dbo", "orders");
        await SeedCorruptTableWithMaskingAsync(id);
        var client = Substitute.For<IOpenMetadataClient>();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>(
        [
            new OpenMetadataTable
            {
                Id = Guid.NewGuid(), Name = "orders", FullyQualifiedName = "svc.corp.dbo.orders",
                Service = new OpenMetadataEntityReference { Name = "svc" },
                DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
                Columns = [new OpenMetadataColumn { Name = "id" }, new OpenMetadataColumn { Name = "ssn" }]
            }
        ]));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]));
        client.GetRolesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));
        var sut = new OpenMetadataSyncService(client, _repo, Substitute.For<IConsentRepository>(), Substitute.For<IPolicyEpochRepository>(),
            Options.Create(new GatewayOptions { OpenMetadata = new OpenMetadataOptions { Enabled = true } }), NullLogger<OpenMetadataSyncService>.Instance);

        var result = await sut.SyncPermissionsAsync(dryRun: false);

        result.SyncedTables.ShouldBe(0);
        MaskingRuleOfSsnMustSurvive();
    }

    [Fact]
    public async Task Dbt_OnUnavailableTable_DoesNotUpsert_MaskingRulesPreserved()
    {
        var id = new TableIdentifier("svc", "dbo", "orders");
        await SeedCorruptTableWithMaskingAsync(id);
        var service = new DbtMetadataIngestionService(Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(),
            NullLogger<DbtMetadataIngestionService>.Instance);
        const string manifest = """
        { "nodes": { "model.p.orders": { "name": "orders", "database": "svc", "schema": "dbo", "description": "d",
          "constraints": [ { "type": "primary_key", "columns": ["id"] } ],
          "columns": { "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }, "ssn": { "name": "ssn", "data_type": "varchar", "meta": {}, "tags": [] } },
          "depends_on": { "nodes": [] } } } }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        var result = await service.IngestManifestStreamAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("unavailable"));
        MaskingRuleOfSsnMustSurvive();
        Scalar("SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS").ShouldBe(1L);
    }

    [Fact]
    public async Task OpenApi_OnUnavailableTable_DoesNotUpsert_MaskingRulesPreserved()
    {
        var id = new TableIdentifier("svc", "api", "order");
        await SeedCorruptTableWithMaskingAsync(id);
        const string spec = """
        { "openapi": "3.0.0", "info": { "title": "S", "version": "1" }, "servers": [ { "url": "https://svc.example.com" } ],
          "paths": { "/orders": { "get": { "summary": "l" } } },
          "components": { "schemas": { "Order": { "type": "object", "x-autheris-primary-key": ["id"],
            "properties": { "id": { "type": "integer" }, "ssn": { "type": "string" } } } } } }
        """;
        var service = new OpenApiIngestionService(_repo, NullLogger<OpenApiIngestionService>.Instance);

        var result = await service.IngestOpenApiJsonAsync(spec, domain: "svc");

        result.Warnings.ShouldContain(w => w.Contains("unavailable"));
        result.IngestedTablesCount.ShouldBe(0);
        MaskingRuleOfSsnMustSurvive();
        TablesNamed("order").ShouldBe(1L, "no second table was created");
    }
}
