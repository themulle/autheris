namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class CatalogChangeIndexerStartupPerfTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid OwnerGroup = new("S-1-5-21-1-2-3-500");

    [Fact]
    public void ComputeAdminRepresentationHash_StreamingWriterMatchesReferenceJsonSerialization()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders_äöü"),
            OwnerTenantId = "tenant-a",
            CatalogTags = ["Finance", "pii", "finance"],
            Columns =
            [
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "zeta_note",
                    DisplayName = "Zeta <script>&\"Note\"",
                    DataType = "nvarchar(200)",
                    Description = "Unicode 😀 € text",
                    WriteProtected = true,
                    DiscoveryHidden = false
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "AlphaSecret",
                    DisplayName = "Alpha",
                    DataType = "varchar(50)",
                    Description = null,
                    DiscoveryHidden = true
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "beta_hmac",
                    DataType = "varchar(50)"
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "clear_id",
                    DataType = "int"
                }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["zeta_note"] = new MaskingRule { RuleType = "Partial" },
                ["beta_hmac"] = new MaskingRule { RuleType = "HMAC" }
            },
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["AlphaSecret"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = Guid.NewGuid().ToString(), Code = "SECRET", Source = "auto", AssignedBy = "sys", AssignedAt = DateTimeOffset.UtcNow }]
            },
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales_sql",
                SourceType = "SqlServer",
                SchemaName = "dbo",
                TableName = "orders_äöü",
                DisplayName = "Orders & Invoices",
                Description = "Line 1\nLine 2 \"quoted\"",
                ObjectKind = DiscoveryObjectKind.Table,
                IsActive = true,
                DiscoveryHidden = false,
                SamplesDisabled = true,
                DocumentationSource = "dbt",
                Tags = ["tier-1", "PII"]
            }
        };

        var owners = new List<ContainerOwnerEntry>
        {
            new() { AdSid = OwnerGroup, DisplayName = "Zed Owner", Email = "zed@example.com", IsActive = true },
            new() { AdSid = new Sid("S-1-5-21-9"), DisplayName = "Inactive", Email = "old@example.com", IsActive = false },
            new() { AdSid = new Sid("S-1-5-21-2"), DisplayName = "Ada Owner", Email = "ada@example.com", IsActive = true }
        };

        var artifacts = new DiscoveryArtifacts(table.Table.Id, "tenant-a", null, null, "deadbeef1234", "admin", DateTimeOffset.UtcNow);
        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroup,
            ProtectionClass.Secret,
            ProtectionClass.Secret,
            null,
            new Dictionary<string, ColumnProtection>(),
            CatalogVisibilityMode.OnRequest);

        var actual = ExternalCatalogReader.ComputeAdminRepresentationHash(table, owners, artifacts, protection);

        var referencePayload = new
        {
            objectId = table.Identifier.ToString(),
            kind = "table",
            domain = table.Identifier.Domain,
            schema = table.Identifier.Schema,
            name = table.Identifier.TableName,
            displayName = table.Table.DisplayName,
            description = table.Table.Description,
            protectionClass = protection.TableClass.ToCanonicalName(),
            isActive = table.Table.IsActive,
            discoveryHidden = table.Table.DiscoveryHidden,
            samplesDisabled = table.Table.SamplesDisabled,
            documentationSource = table.Table.DocumentationSource,
            tags = table.CatalogTags.Concat(table.Table.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            owners = owners.Where(static o => o.IsActive).Select(static o => $"{o.DisplayName}|{o.Email}").Order(StringComparer.Ordinal).ToArray(),
            columns = table.Columns
                .OrderBy(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new
                {
                    c.ColumnName,
                    c.DisplayName,
                    c.DataType,
                    c.Description,
                    Classified = ClassificationMetadata.IsColumnSensitive(table, c),
                    c.DiscoveryHidden,
                    c.WriteProtected,
                    masking = c.ColumnName == "beta_hmac"
                        ? "pseudonymised"
                        : (c.ColumnName == "clear_id" ? "clear" : "masked")
                })
                .ToArray(),
            artifactsHash = artifacts.ContentSha256
        };
        var referenceJson = JsonSerializer.Serialize(referencePayload);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(referenceJson)));

        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task ContainerOwnership_GetDataOwnersForTableMetadataAsync_SkipsStructuralMetadataBatchRead_WhenTableIdPresent()
    {
        var withId = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: TenantA.Value);
        var withoutId = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "legacy_empty_id"),
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = Guid.Empty, SourceName = "sales", SchemaName = "dbo", TableName = "legacy_empty_id", IsActive = true }
        };

        var metaRepo = new SpyMetadataRepository([withId, withoutId]);
        var domainId = Guid.NewGuid();
        var container = new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, domainId),
            OwnerGroup,
            ProtectionClass.Internal,
            CatalogVisibilityMode.OnRequest,
            IsValid: true);
        var domains = new StubDomainRepo(container, contact: "owners@example.com");
        var ownership = new ContainerOwnership(metaRepo, domains, catalogOwnerTenant: TenantA);

        var fastOwners = await ownership.GetDataOwnersForTableMetadataAsync([withId]);
        metaRepo.StructuralBatchCalls.ShouldBe(0);
        fastOwners[withId.Identifier].ShouldHaveSingleItem().AdSid.ShouldBe(OwnerGroup);

        var fallbackOwners = await ownership.GetDataOwnersForTableMetadataAsync([withoutId]);
        metaRepo.StructuralBatchCalls.ShouldBe(1);
        fallbackOwners.ContainsKey(withoutId.Identifier).ShouldBeTrue();
    }

    [Fact]
    public async Task CatalogChangeIndexer_IndexOnceAsync_UsesSnapshotAndPreloadedMetadata_WithoutExtraTableReads()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: TenantA.Value);
        var metaRepo = new SpyMetadataRepository([table]);
        var domain = new GovernanceDomain(Guid.NewGuid(), TenantA, "sales", "Sales", "owners@example.com", OwnerGroup, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var domains = new StubDomainRepo(
            new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domain.Id), OwnerGroup, ProtectionClass.Internal, CatalogVisibilityMode.Open, IsValid: true),
            contact: "owners@example.com",
            domainList: [domain]);
        var ownership = new ContainerOwnership(metaRepo, domains, catalogOwnerTenant: TenantA);
        var changeStore = new InMemoryCatalogChangeStore();
        var snapshotProvider = new StaticSnapshotProvider([table], new CatalogVersion(2, 1));
        var options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = TenantA.Value } });

        using var indexer = new CatalogChangeIndexerHostedService(
            metaRepo,
            new FakeDiscoveryConsentRepository(),
            ownership,
            changeStore,
            options,
            snapshotProvider: snapshotProvider,
            domainRepository: domains);

        var written = await indexer.IndexOnceAsync();
        written.ShouldBe(1);
        snapshotProvider.GetCalls.ShouldBe(1);
        metaRepo.WholeCatalogReads.ShouldBe(0);
        metaRepo.StructuralBatchCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SearchWarmupAndDeniedReporter_UseSnapshotProviderOnStartup_AndFallBackWhenNotReady()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: TenantA.Value);
        var repo = new SpyMetadataRepository([table]);
        var searchEngine = new SpySearchEngine();
        var snapshotProvider = new StaticSnapshotProvider([table], new CatalogVersion(1, 1));

        using var warmup = new CatalogSearchWarmupService(
            repo,
            searchEngine,
            Options.Create(new CatalogSearchOptions { WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            timeProvider: new FakeTimeProvider(),
            snapshotProvider: snapshotProvider);

        await warmup.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => searchEngine.RebuildCalls >= 1, TimeSpan.FromSeconds(5));
        await warmup.StopAsync(CancellationToken.None);

        repo.WholeCatalogReads.ShouldBe(0);
        snapshotProvider.GetCalls.ShouldBe(1);

        var reporterLogger = new CapturingLogger<DeniedTablesStartupReporter>();
        using var reporter = new DeniedTablesStartupReporter(
            repo,
            profiles: null,
            virtualFilters: null,
            Options.Create(new GatewayOptions()),
            reporterLogger,
            snapshotProvider: snapshotProvider);
        await reporter.StartAsync(CancellationToken.None);
        await reporter.ExecuteTask!;

        repo.WholeCatalogReads.ShouldBe(0);
        snapshotProvider.GetCalls.ShouldBe(2);

        var notReadySnapshot = new ThrowingNotReadySnapshotProvider();
        using var fallbackReporter = new DeniedTablesStartupReporter(
            repo,
            profiles: null,
            virtualFilters: null,
            Options.Create(new GatewayOptions()),
            new CapturingLogger<DeniedTablesStartupReporter>(),
            snapshotProvider: notReadySnapshot);
        await fallbackReporter.StartAsync(CancellationToken.None);
        await fallbackReporter.ExecuteTask!;

        repo.WholeCatalogReads.ShouldBe(1);
    }

    private sealed class StaticSnapshotProvider(IReadOnlyList<TableMetadata> tables, CatalogVersion version) : ICatalogSnapshotProvider
    {
        private int _calls;
        public int GetCalls => Volatile.Read(ref _calls);
        public CatalogSnapshot CurrentSnapshot => CatalogSnapshot.Create(version, tables, []);
        public bool IsInitialized => true;
        public bool IsDegraded => false;

        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(CurrentSnapshot);
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignalChange() { }
    }

    private sealed class ThrowingNotReadySnapshotProvider : ICatalogSnapshotProvider
    {
        public CatalogSnapshot CurrentSnapshot => CatalogSnapshot.Empty(new CatalogVersion(1, 1));
        public bool IsInitialized => false;
        public bool IsDegraded => false;
        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default) => throw new CatalogSnapshotNotReadyException();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignalChange() { }
    }

    private sealed class SpySearchEngine : ICatalogSearchEngine
    {
        private int _rebuildCalls;
        public int RebuildCalls => Volatile.Read(ref _rebuildCalls);
        public bool IsIndexReady => RebuildCalls > 0;
        public int IndexedTableCount { get; private set; }
        public DateTimeOffset? LastIndexedAt { get; private set; }
        public Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
        {
            IndexedTableCount = tables.Count;
            LastIndexedAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _rebuildCalls);
            return Task.CompletedTask;
        }
        public Task ApplyDeltaAsync(IReadOnlyList<TableMetadata> updated, IReadOnlyList<TableIdentifier> removed, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<CatalogSearchHit> Search(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null) => [];
        public Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CatalogSearchHit>>([]);
    }

    private sealed class SpyMetadataRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        private int _wholeReads;
        private int _structuralBatchCalls;
        public int WholeCatalogReads => Volatile.Read(ref _wholeReads);
        public int StructuralBatchCalls => Volatile.Read(ref _structuralBatchCalls);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _wholeReads);
            return Task.FromResult(tables);
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _wholeReads);
            return Task.FromResult(new CatalogTablePage(tables.ToList(), null));
        }

        public Task<IReadOnlyList<TableMetadata?>> GetStructuralTableMetadataBatchAsync(IReadOnlyList<TableIdentifier> ids, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _structuralBatchCalls);
            return Task.FromResult<IReadOnlyList<TableMetadata?>>(
                ids.Select(id => tables.FirstOrDefault(t => t.Identifier.Equals(id))).ToList());
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubDomainRepo(
        ContainerDefaults container,
        string? contact = null,
        IReadOnlyList<GovernanceDomain>? domainList = null) : IGovernanceDomainRepository
    {
        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => Task.FromResult(container);
        public Task<IReadOnlyDictionary<Guid, ContainerDefaults>> GetContainersForTablesAsync(TenantId tenant, IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, ContainerDefaults>>(tableIds.ToDictionary(static id => id, _ => container));
        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default)
            => Task.FromResult(contact is null ? null : new GovernanceDomain(domainId, tenant, "sales", "Sales", contact, OwnerGroup, ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default)
            => Task.FromResult(domainList ?? Array.Empty<GovernanceDomain>());
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
