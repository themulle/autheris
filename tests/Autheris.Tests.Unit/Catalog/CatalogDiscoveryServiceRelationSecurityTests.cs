namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Service-level checks of the join relations of a search hit on both search paths (discovery service and legacy engine path):
/// the stored relations are listed, a target the caller cannot see never appears in the response.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryServiceRelationSecurityTests
{
    private const string HiddenTarget = "vault_secrets";

    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-USER-1185");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Orders = new("crm", "dbo", "orders");
    private static readonly TableIdentifier Customers = new("crm", "dbo", "customers");
    private static readonly TableIdentifier Vault = new("crm", "dbo", HiddenTarget);

    private static TableMetadata Table(TableIdentifier id, string owner, string description, params string[] columns) => new()
    {
        Identifier = id,
        OwnerTenantId = owner,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "dbo", TableName = id.TableName, DisplayName = id.TableName, Description = description, IsActive = true },
        Columns = columns.Select(c => new TableColumn { Id = Guid.NewGuid(), ColumnName = c, DataType = "int", DisplayName = c }).ToList()
    };

    private static TableMetadata[] Catalog() =>
    [
        Table(Orders, Tenant.Value, "Sales order headers", "id", "customer_id", "vault_id"),
        Table(Customers, Tenant.Value, "Customer accounts", "id"),
        Table(Vault, "tenant-b", "Other tenant vault", "id")
    ];

    private static Consent Grant(TableMetadata table) => new()
    {
        TableId = table.Table.Id,
        TableIdentifier = table.Identifier,
        TenantId = Tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = table.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList()
    };

    private static TableRelation Relation(string name, TableIdentifier parent, TableIdentifier child, string childKey) => new()
    {
        RelationName = name,
        ParentTableIdentifier = parent,
        ChildTableIdentifier = child,
        Cardinality = RelationCardinality.OneToMany,
        JoinKeysParent = ["id"],
        JoinKeysChild = [childKey]
    };

    private static async Task<CatalogDiscoveryService> CreateAsync(bool discoveryPath)
    {
        var tables = Catalog();
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.AddRange(tables);
        var engine = new CatalogSearchEngine(
            new ConstantVectorGenerator(),
            Options.Create(new CatalogSearchOptions()),
            NullLogger<CatalogSearchEngine>.Instance,
            new AdjustableClassificationTimeProvider(FixedNow));
        await engine.RebuildIndexAsync(tables);
        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var consents = new TestInMemoryConsentRepository(tables.Where(t => t.OwnerTenantId == Tenant.Value).Select(Grant).ToList());

        DiscoveryService? discovery = null;
        if (discoveryPath)
        {
            discovery = new DiscoveryService(
                repo,
                new FakeAccessDecisionPoint(req => TableAccessDecision.Allowed(
                    req.Object.Identifier,
                    req.Object.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase))),
                new FakeDiscoveryConsentRepository(),
                new FakeDiscoveryApprovalRepository(),
                new FakeDiscoveryOwnershipRepository(),
                new FakeDiscoveryArtifactStore(),
                new FakeAuditWriter(),
                Options.Create(options),
                Options.Create(new CatalogDiscoveryOptions()),
                searchEngine: engine);
        }

        return new CatalogDiscoveryService(
            repo,
            new FakeAuditWriter(),
            Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(repo, consents, options),
            searchEngine: engine,
            consentRepository: consents,
            graphQlMap: new CatalogGraphQlMap(
                repo,
                new StaticRelations(
                    Relation("customer_orders", Customers, Orders, "customer_id"),
                    Relation("vault_orders", Vault, Orders, "vault_id"))),
            discoveryService: discovery);
    }

    private static async Task<(CatalogSearchHit Hit, string Json)> SearchOrdersAsync(bool discoveryPath)
    {
        var service = await CreateAsync(discoveryPath);
        var context = new RequestContext(Tenant, UserSid, User: DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value));

        var hits = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("orders", Mode: CatalogSearchMode.KeywordBm25), context);

        var hit = hits.Single(h => h.TableIdentifier == Orders);
        return (hit, JsonSerializer.Serialize(hits));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchCatalogDetailed_StoredRelations_ListedOnBothPaths(bool discoveryPath)
    {
        var (hit, _) = await SearchOrdersAsync(discoveryPath);

        var relation = hit.RelatedJoinPaths.ShouldHaveSingleItem();
        (relation.FromTable, relation.FromColumn, relation.ToTable, relation.ToColumn).ShouldBe((Orders, "customer_id", Customers, "id"));
        relation.Incoming.ShouldBeFalse();
    }

    [Fact]
    public Task SearchCatalogDetailed_DiscoveryPath_HiddenTargetNotInResponse() => AssertHiddenTargetAbsentAsync(discoveryPath: true);

    [Fact]
    public Task SearchCatalogDetailed_LegacyPath_HiddenTargetNotInResponse() => AssertHiddenTargetAbsentAsync(discoveryPath: false);

    private static async Task AssertHiddenTargetAbsentAsync(bool discoveryPath)
    {
        var (hit, json) = await SearchOrdersAsync(discoveryPath);

        json.ShouldNotContain(HiddenTarget);
        hit.RelatedJoinPaths.ShouldAllBe(r => r.ToTable != Vault);
    }

    private sealed class ConstantVectorGenerator : ILocalEmbeddingGenerator
    {
        public int EmbeddingDimensions => 4;
        public string ModelIdentifier => "constant-test";
        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) => new float[] { 1, 0, 0, 0 };

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1, 0, 0, 0 }).ToList());
    }

    private sealed class StaticRelations(params TableRelation[] relations) : ITableRelationRepository
    {
        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>(relations.Where(r => r.ParentTableIdentifier.Equals(parentTable)).ToList());

        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>(relations.ToList());

        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
