namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Extensions.OpenMetadata;
using Autheris.Tests;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClassificationIngestionSyncTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static TableMetadata SeedExistingTable(CountingBatchTableMetadataRepository metaRepo, string domain, string schema, string table, Guid? emailColId = null, Guid? secretColId = null)
    {
        var tId = new TableIdentifier(domain, schema, table);
        var tableGuid = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = tId,
            Table = new Table { Id = tableGuid, SourceName = domain, SchemaName = schema, TableName = table, SourceType = "PostgreSQL", IsActive = true, DataSourceType = DataSourceType.Sql },
            Columns =
            [
                new TableColumn { Id = emailColId ?? Guid.NewGuid(), TableId = tableGuid, ColumnName = "email", DataType = "varchar" },
                new TableColumn { Id = secretColId ?? Guid.NewGuid(), TableId = tableGuid, ColumnName = "token", DataType = "varchar" }
            ]
        };
        metaRepo.Seed(meta);
        return meta;
    }

    [Fact]
    public async Task Dbt_meta_maps_to_global_ingestion_and_preserves_owner_and_admin_rows_on_reingest()
    {
        var emailColId = Guid.NewGuid();
        var secretColId = Guid.NewGuid();
        var metaRepo = new CountingBatchTableMetadataRepository();
        SeedExistingTable(metaRepo, "crm", "analytics", "customers", emailColId, secretColId);
        var classRepo = new TestInMemoryClassificationStore("tenant-a");
        var time = new AdjustableClassificationTimeProvider(FixedNow);

        await classRepo.AssignColumnClassificationAsync(new ColumnClassification
        {
            Scope = ClassificationScopes.Global,
            TableColumnId = emailColId.ToString(),
            Code = ClassificationScopes.PiiSpecialCode,
            Source = "OWNER",
            AssignedBy = "owner-1",
            AssignedAt = FixedNow.AddDays(-2)
        });
        await classRepo.AssignColumnClassificationAsync(new ColumnClassification
        {
            Scope = ClassificationScopes.Global,
            TableColumnId = secretColId.ToString(),
            Code = ClassificationScopes.CredentialsCode,
            Source = "ADMIN",
            AssignedBy = "admin-1",
            AssignedAt = FixedNow.AddDays(-1)
        });

        var options = Options.Create(new GatewayOptions { Dbt = new DbtOptions { ActivateNewTables = false } });
        var svc = new DbtMetadataIngestionService(
            new FakeDbtProposalRepository(), metaRepo, new FakeLineageGraphStore(), epochRepository: null,
            options, sqlEndpointLoader: null, relationRepository: null, virtualFilterAdmin: null,
            tagImport: null, artifactStore: null, testTargetMap: null,
            NullLogger<DbtMetadataIngestionService>.Instance, new NullAuditLogRepository(),
            classificationRepository: classRepo, timeProvider: time);

        const string manifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.analytics.customers": {
              "name": "customers",
              "database": "crm",
              "schema": "analytics",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": {},
              "columns": {
                "email": { "name": "email", "data_type": "varchar", "tags": [], "meta": { "pii": "true", "classification": "PII_SPECIAL" } },
                "token": { "name": "token", "data_type": "varchar", "tags": [], "meta": { "classification": "PUBLIC" } }
              }
            }
          }
        }
        """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false, CancellationToken.None);
        result.ShouldNotBeNull();

        var emailRows = classRepo.ColumnClassifications.Where(c => c.TableColumnId == emailColId.ToString()).ToList();
        emailRows.ShouldContain(c => c.Scope == ClassificationScopes.Global && c.Code == ClassificationScopes.PiiSpecialCode && c.Source == "OWNER");
        emailRows.ShouldContain(c => c.Scope == ClassificationScopes.Global && c.Code == ClassificationScopes.PiiCode && c.Source == "INGESTION");

        // Re-ingestion preserves ADMIN CREDENTIALS row untouched and never widens/deletes it.
        var tokenRows = classRepo.ColumnClassifications.Where(c => c.TableColumnId == secretColId.ToString()).ToList();
        tokenRows.ShouldContain(c => c.Code == ClassificationScopes.CredentialsCode && c.Source == "ADMIN");
        await using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        await svc.IngestManifestStreamAsClusterAdminAsync(stream2, dryRun: false, CancellationToken.None);
        classRepo.ColumnClassifications.Count(c => c.TableColumnId == emailColId.ToString() && c.Code == ClassificationScopes.PiiCode).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_dbt_and_openmetadata_codes_fail_closed_and_never_widen_to_public()
    {
        var emailColId = Guid.NewGuid();
        var secretColId = Guid.NewGuid();
        var metaRepo = new CountingBatchTableMetadataRepository();
        SeedExistingTable(metaRepo, "crm", "public", "leads", emailColId, secretColId);
        var classRepo = new TestInMemoryClassificationStore("tenant-a");
        var time = new AdjustableClassificationTimeProvider(FixedNow);

        var dbtOptions = Options.Create(new GatewayOptions { Dbt = new DbtOptions { ActivateNewTables = false } });
        var dbtSvc = new DbtMetadataIngestionService(
            new FakeDbtProposalRepository(), metaRepo, new FakeLineageGraphStore(), epochRepository: null,
            dbtOptions, sqlEndpointLoader: null, relationRepository: null, virtualFilterAdmin: null,
            tagImport: null, artifactStore: null, testTargetMap: null,
            NullLogger<DbtMetadataIngestionService>.Instance, new NullAuditLogRepository(),
            classificationRepository: classRepo, timeProvider: time);

        const string manifestUnknown = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.public.leads": {
              "name": "leads",
              "database": "crm",
              "schema": "public",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": {},
              "columns": {
                "email": { "name": "email", "data_type": "varchar", "tags": [], "meta": { "classification": "UNKNOWN_VENDOR_TIER,PUBLIC" } }
              }
            }
          }
        }
        """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestUnknown));
        var dbtRes = await dbtSvc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false, CancellationToken.None);
        dbtRes.Warnings.ShouldContain(w => w.Contains("UNKNOWN_VENDOR_TIER", StringComparison.Ordinal));
        var dbtEmailRows = classRepo.ColumnClassifications.Where(c => c.TableColumnId == emailColId.ToString()).ToList();
        dbtEmailRows.ShouldNotContain(c => c.Code == ClassificationScopes.PublicCode);
        dbtEmailRows.ShouldContain(c => c.Scope == ClassificationScopes.Global && c.Source == "INGESTION" && c.Code == ClassificationScopes.ConfidentialCode);

        // Now verify OpenMetadata mapping: Art9 -> PII_SPECIAL, unknown Classification tag -> fail closed, never PUBLIC.
        var omTable = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "accounts",
            FullyQualifiedName = "crm.crm.public.accounts",
            ServiceType = "PostgreSQL",
            Service = new OpenMetadataEntityReference { Name = "crm" },
            Database = new OpenMetadataEntityReference { Name = "crm" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "public" },
            Columns =
            [
                new OpenMetadataColumn { Name = "health_flag", DataType = "varchar", Tags = [new OpenMetadataTag { TagFQN = "GDPR.Article9" }] },
                new OpenMetadataColumn { Name = "raw_ext", DataType = "varchar", Tags = [new OpenMetadataTag { TagFQN = "Classification.MYSTERY_TIER" }, new OpenMetadataTag { TagFQN = "Classification.PUBLIC" }] }
            ]
        };

        var omOptions = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" },
            OpenMetadata = new OpenMetadataOptions { Enabled = true, ServiceDatabaseToDomainMap = new() { ["crm.crm"] = "crm" } }
        });
        var omSvc = new OpenMetadataSyncService(
            new FakeOpenMetadataClient([omTable]), metaRepo, new TestInMemoryConsentRepository([]), new FakeEpochRepository(),
            omOptions, NullLogger<OpenMetadataSyncService>.Instance, classificationRepo: classRepo, timeProvider: time);

        var omRes = await omSvc.SyncPermissionsAsync(dryRun: false, ct: CancellationToken.None);
        omRes.Warnings.ShouldContain(w => w.Contains("MYSTERY_TIER", StringComparison.Ordinal));
        var storedAcc = await metaRepo.GetTableMetadataAsync(new TableIdentifier("crm", "public", "accounts"));
        storedAcc.ShouldNotBeNull();
        var healthCol = storedAcc!.Columns.Single(c => c.ColumnName == "health_flag");
        var rawCol = storedAcc.Columns.Single(c => c.ColumnName == "raw_ext");

        classRepo.ColumnClassifications.ShouldContain(c => c.TableColumnId == healthCol.Id.ToString() && c.Scope == ClassificationScopes.Global && c.Code == ClassificationScopes.PiiSpecialCode && c.Source == "INGESTION");
        classRepo.ColumnClassifications.Where(c => c.TableColumnId == rawCol.Id.ToString()).ShouldNotContain(c => c.Code == ClassificationScopes.PublicCode);
        classRepo.ColumnClassifications.ShouldContain(c => c.TableColumnId == rawCol.Id.ToString() && c.Code == ClassificationScopes.ConfidentialCode && c.Source == "INGESTION");
    }

    private sealed class FakeDbtProposalRepository : IDbtProposalRepository
    {
        public Task<DbtMetadataProposal> AddProposalAsync(DbtMetadataProposal proposal, CancellationToken ct = default) => Task.FromResult(proposal);
        public Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DbtMetadataProposal>>([]);
        public Task<DbtMetadataProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default) => Task.FromResult<DbtMetadataProposal?>(null);
        public Task<DbtMetadataProposal> UpdateProposalStatusAsync(Guid proposalId, DbtProposalStatus status, string reviewedBy, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeLineageGraphStore : ILineageGraphStore
    {
        public LineageNode? GetNode(string nodeId) => null;
        public bool ContainsNode(string nodeId) => false;
        public void UpdateGraph(IEnumerable<LineageNode> nodes) { }
        public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
        public int Count => 0;
    }

    private sealed class FakeEpochRepository : IPolicyEpochRepository
    {
        public Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(2L);
    }

    private sealed class FakeOpenMetadataClient(IReadOnlyList<OpenMetadataTable> tables) : IOpenMetadataClient
    {
        public Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default) => Task.FromResult(tables);
        public Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default) => Task.FromResult(tables.FirstOrDefault(t => string.Equals(t.FullyQualifiedName, fqn, StringComparison.OrdinalIgnoreCase)));
        public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]);
        public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]);
        public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]);
        public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]);
    }
}
