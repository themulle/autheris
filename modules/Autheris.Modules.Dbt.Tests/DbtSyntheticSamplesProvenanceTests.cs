namespace Autheris.Modules.Dbt.Tests;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Lineage;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DbtSyntheticSamplesProvenanceTests
{
    private static readonly TenantId Tenant = new("warehouse");

    private static (DbtMetadataIngestionService Service, StubTableRepo Tables, RecordingArtifactStore Artifacts, RecordingAuditWriter Audit, RecordingNotificationDispatcher Notifications) CreateHarness()
    {
        var options = new GatewayOptions
        {
            Dbt = new DbtOptions { CreateMissingTables = true, ActivateNewTables = true },
            Tenancy = new TenancyOptions { DefaultTenantId = Tenant.Value }
        };
        options.DataSources.Connections["warehouse"] = new DataSourceConnectionOptions { Provider = "PostgreSql" };

        var tables = new StubTableRepo();
        var artifacts = new RecordingArtifactStore();
        var auditWriter = new RecordingAuditWriter();
        var notifications = new RecordingNotificationDispatcher();

        var service = new DbtMetadataIngestionService(
            proposalRepository: new InMemoryDbtProposalRepository(),
            metadataRepository: tables,
            lineageGraphStore: new StubLineageStore(),
            epochRepository: null,
            gatewayOptions: Options.Create(options),
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: null,
            tagImport: null,
            artifactStore: artifacts,
            testTargetMap: null,
            logger: NullLogger<DbtMetadataIngestionService>.Instance,
            auditLog: new RecordingAuditRepo(),
            auditWriter: auditWriter,
            notificationDispatcher: notifications);

        return (service, tables, artifacts, auditWriter, notifications);
    }

    private static string BuildManifest(IReadOnlyList<Dictionary<string, object?>> samples)
    {
        var manifest = new
        {
            metadata = new { dbt_version = "1.8.0" },
            nodes = new Dictionary<string, object>
            {
                ["model.proj.customers"] = new
                {
                    name = "customers",
                    database = "warehouse",
                    schema = "analytics",
                    config = new { materialized = "table" },
                    meta = new Dictionary<string, object?> { ["synthetic_samples"] = samples },
                    columns = new Dictionary<string, object>
                    {
                        ["id"] = new { name = "id", data_type = "bigint" },
                        ["contact"] = new { name = "contact", data_type = "varchar" }
                    },
                    depends_on = new { nodes = Array.Empty<string>() }
                }
            }
        };
        return JsonSerializer.Serialize(manifest);
    }

    [Fact]
    public async Task Audit_event_with_hash()
    {
        var (service, tables, artifacts, audit, _) = CreateHarness();
        var json = BuildManifest([new() { ["id"] = 1, ["contact"] = "example-city" }]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sync = await service.IngestManifestStreamAsClusterAdminAsync(stream);

        sync.Success.ShouldBeTrue();
        var meta = await tables.GetTableMetadataAsync(new TableIdentifier("warehouse", "analytics", "customers"));
        meta.ShouldNotBeNull();
        var stored = await artifacts.GetAsync(meta!.Table.Id, ArtifactProvenance.Import);
        stored.ShouldNotBeNull();
        stored!.Provenance.ShouldBe(ArtifactProvenance.Import);
        stored.StatisticsJson.ShouldBeNull();

        var entry = audit.Entries.Single(e => e.EventType == AuditEventTypes.ImportSamplesChanged);
        entry.DetailsJson.ShouldNotBeNull();
        entry.DetailsJson!.ShouldContain(stored.ContentSha256);

        var oversizedRows = Enumerable.Range(1, 50)
            .Select(i => Enumerable.Range(0, 10).ToDictionary(k => k == 0 ? "id" : $"c{k}", k => (object?)(k == 0 ? i : new string('x', 200))))
            .ToList();
        var tooLargeColumns = Enumerable.Range(0, 10)
            .ToDictionary(k => k == 0 ? "id" : $"c{k}", k => (object)new { name = k == 0 ? "id" : $"c{k}", data_type = "varchar" });
        var bigManifest = JsonSerializer.Serialize(new
        {
            metadata = new { dbt_version = "1.8.0" },
            nodes = new Dictionary<string, object>
            {
                ["model.proj.huge"] = new
                {
                    name = "huge",
                    database = "warehouse",
                    schema = "analytics",
                    config = new { materialized = "table" },
                    meta = new Dictionary<string, object?> { ["synthetic_samples"] = oversizedRows },
                    columns = tooLargeColumns,
                    depends_on = new { nodes = Array.Empty<string>() }
                }
            }
        });
        using var bigStream = new MemoryStream(Encoding.UTF8.GetBytes(bigManifest));
        var bigSync = await service.IngestManifestStreamAsClusterAdminAsync(bigStream);
        bigSync.Warnings.ShouldContain(w => w.Contains("SYNTHETIC_SAMPLE_SIZE_EXCEEDED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pii_pattern_raises_event_not_block()
    {
        var (service, tables, artifacts, audit, notifications) = CreateHarness();
        var json = BuildManifest([new() { ["id"] = 1, ["contact"] = "real.person@company.com" }]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sync = await service.IngestManifestStreamAsClusterAdminAsync(stream);

        sync.Success.ShouldBeTrue();
        var meta = await tables.GetTableMetadataAsync(new TableIdentifier("warehouse", "analytics", "customers"));
        meta.ShouldNotBeNull();
        var stored = await artifacts.GetAsync(meta!.Table.Id, ArtifactProvenance.Import);
        stored.ShouldNotBeNull();

        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ImportSamplesPiiSuspected);
        notifications.Messages.ShouldContain(m => m.EventType == AuditEventTypes.ImportSamplesPiiSuspected);
    }

    [Fact]
    public async Task Quarantine_policy_suppresses()
    {
        var (_, tables, artifacts, audit, _) = CreateHarness();
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("warehouse", "analytics", "customers"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "warehouse",
                SchemaName = "analytics",
                TableName = "customers",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "contact", DataType = "varchar" }
            ],
            OwnerTenantId = Tenant.Value
        };
        await tables.UpsertTableMetadataAsync(table);
        await artifacts.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id": 1, "contact": "real.person@company.com"}]""",
            null,
            "sha-pii",
            "dbt-ingestion",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Import), new Sid("S-1-5-21-SYS-1"));

        var gatewayOpts = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = Tenant.Value } });
        var discOpts = Options.Create(new CatalogDiscoveryOptions());

        var suppressedSvc = new DiscoveryArtifactService(
            tables,
            artifacts,
            new AllowOwnershipRepo(),
            audit,
            new GatewayRoleEvaluator(),
            gatewayOpts,
            discOpts,
            accessPolicySettings: new StubPolicyProvider(serveImportSamples: true, quarantineSuspectedPii: true));

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "S-1-5-21-U1"),
            new System.Security.Claims.Claim("tenant_id", Tenant.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        ], "Bearer"));

        var suppressed = await suppressedSvc.GetSamplesAsync(caller, table.Identifier.ToString());
        suppressed.Payload!.Available.ShouldBeFalse();

        var unsuppressedSvc = new DiscoveryArtifactService(
            tables,
            artifacts,
            new AllowOwnershipRepo(),
            audit,
            new GatewayRoleEvaluator(),
            gatewayOpts,
            discOpts,
            accessPolicySettings: new StubPolicyProvider(serveImportSamples: true, quarantineSuspectedPii: false));

        var allowed = await unsuppressedSvc.GetSamplesAsync(caller, table.Identifier.ToString());
        allowed.Payload!.Available.ShouldBeTrue();
        allowed.Payload.Samples.Count.ShouldBe(1);
    }

    private sealed class RecordingArtifactStore : IDiscoveryArtifactStore
    {
        public Dictionary<(Guid TableId, ArtifactProvenance Provenance), DiscoveryArtifacts> Items { get; } = [];

        public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default)
            => Task.FromResult(
                Items.TryGetValue((tableId, ArtifactProvenance.Upload), out var up) ? up :
                Items.TryGetValue((tableId, ArtifactProvenance.Import), out var imp) ? imp : null);

        public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, ArtifactProvenance provenance, CancellationToken ct = default)
            => Task.FromResult(Items.TryGetValue((tableId, provenance), out var item) ? item : null);

        public Task<IReadOnlyList<DiscoveryArtifacts>> GetAllAsync(Guid tableId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscoveryArtifacts>>(Items.Where(kv => kv.Key.TableId == tableId).Select(kv => kv.Value).ToList());

        public Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
        {
            if (artifacts.Provenance == ArtifactProvenance.Import && !string.IsNullOrWhiteSpace(artifacts.StatisticsJson))
            {
                throw new ArgumentException("Import discovery artifacts must not carry statistics.", nameof(artifacts));
            }

            Items[(artifacts.TableId, artifacts.Provenance)] = artifacts;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default)
        {
            var keys = Items.Keys.Where(k => k.TableId == tableId).ToList();
            foreach (var k in keys) Items.Remove(k);
            return Task.FromResult(keys.Count > 0);
        }

        public Task<bool> DeleteAsync(Guid tableId, ArtifactProvenance provenance, Sid actor, CancellationToken ct = default)
            => Task.FromResult(Items.Remove((tableId, provenance)));
    }

    private sealed class StubTableRepo : ITableMetadataRepository
    {
        private readonly Dictionary<string, TableMetadata> _tables = new(StringComparer.OrdinalIgnoreCase);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(_tables.TryGetValue(table.ToString(), out var m) ? m : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables[metadata.Identifier.ToString()] = metadata;
            return Task.FromResult(metadata);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => Task.FromResult<TableKeyDefinitionChange?>(null);

        public ValueTask<long> FlushCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(1L);
    }

    private sealed class StubLineageStore : ILineageGraphStore
    {
        public LineageNode? GetNode(string nodeId) => null;
        public bool ContainsNode(string nodeId) => false;
        public void UpdateGraph(IEnumerable<LineageNode> nodes) { }
        public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
        public int Count => 0;
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
    }

    private sealed class RecordingAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class RecordingNotificationDispatcher : IGovernanceNotificationDispatcher
    {
        public List<GovernanceNotificationMessage> Messages { get; } = [];
        public Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StubPolicyProvider(bool serveImportSamples, bool quarantineSuspectedPii) : IAccessPolicySettingsProvider
    {
        private readonly TenantAccessPolicy _policy = TenantAccessPolicy.FromDeploymentDefaults(
            Tenant,
            new AccessPolicyOptions
            {
                ServeImportSamples = serveImportSamples,
                QuarantineSuspectedPiiSamples = quarantineSuspectedPii
            });

        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(_policy);
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class AllowOwnershipRepo : IContainerOwnership
    {
        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DataOwner>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>());
        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
