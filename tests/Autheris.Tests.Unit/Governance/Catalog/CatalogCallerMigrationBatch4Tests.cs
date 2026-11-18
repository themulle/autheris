namespace Autheris.Tests.Unit.Governance.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The last full-load callers of the catalog read through keyset pages; rows read stay proportional to the request.</summary>
[Trait("Category", "Security")]
public sealed class CatalogCallerMigrationBatch4Tests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private static TableMetadata SqlTable(string name, bool active = true) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "public",
            TableName = name,
            DisplayName = name,
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            IsActive = active
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static IReadOnlyList<TableMetadata> Tables(int count) =>
        Enumerable.Range(0, count).Select(i => SqlTable($"t_{i:D3}")).ToList();

    [Fact]
    public async Task SnapshotProvider_FullLoad_ReadsKeysetPages_NeverGetAllTablesAsync()
    {
        var catalog = new CountingCatalogRepository(Tables(5), pageSize: 2);
        var repo = Substitute.For<IGovernanceRepository>();
        repo.QueryTablesAsync(Arg.Any<CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => catalog.QueryTablesAsync(call.Arg<CatalogTableQuery>(), call.Arg<CancellationToken>()));
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>()));
        var versions = Substitute.For<ICatalogVersionProvider>();
        versions.GetCatalogVersionAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        await using var provider = new CatalogSnapshotProvider(repo, versions);
        var snapshot = await provider.GetSnapshotAsync();

        snapshot.TableCount.ShouldBe(5);
        catalog.GetAllCalls.ShouldBe(0);
        catalog.QueryCalls.Count.ShouldBe(3);
        await repo.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
    }

    [Fact]
    public async Task SearchWarmup_FullRebuild_ReadsKeysetPages_NeverGetAllTablesAsync()
    {
        var catalog = new CountingCatalogRepository(Tables(5), pageSize: 2);
        var engine = Substitute.For<ICatalogSearchEngine>();
        IReadOnlyList<TableMetadata>? indexed = null;
        engine.RebuildIndexAsync(Arg.Do<IReadOnlyList<TableMetadata>>(t => indexed = t), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var service = new CatalogSearchWarmupService(
            catalog,
            engine,
            Options.Create(new CatalogSearchOptions { Enabled = true, WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        await WaitHelper.WaitUntil(() => indexed is not null, TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        indexed.ShouldNotBeNull().Count.ShouldBe(5);
        catalog.GetAllCalls.ShouldBe(0);
        catalog.QueryCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task CatalogSchemaModel_Build_ReadsKeysetPages_NeverGetAllTablesAsync()
    {
        var catalog = new CountingCatalogRepository(Tables(5), pageSize: 2);
        var relations = Substitute.For<ITableRelationRepository>();
        relations.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(catalog, relations);

        model.Tables.Count.ShouldBe(5);
        catalog.GetAllCalls.ShouldBe(0);
        catalog.QueryCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Discovery_List_ReadsKeysetPages_NeverGetAllTablesAsync()
    {
        var tables = Enumerable.Range(0, 5).Select(i => DiscoveryTestBuilder.MakeTable("sales", $"t_{i}")).ToList();
        var catalog = new CountingCatalogRepository(tables, pageSize: 2);

        var page = await CreateDiscovery(catalog).ListAsync(new DiscoveryQuery(), CallerOf());

        page.TotalCount.ShouldBe(5);
        catalog.GetAllCalls.ShouldBe(0);
        catalog.QueryCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task HiddenTables_AppliedSameAsFullLoad_AcrossPageSizes()
    {
        var visible = DiscoveryTestBuilder.MakeTable("sales", "visible");
        var hidden = DiscoveryTestBuilder.MakeTable("sales", "hidden_one", hidden: true);
        var foreignVirtual = DiscoveryTestBuilder.MakeTable("sales", "foreign_virtual", ownerTenantId: "tenant-b", virtualTable: true);
        var inactive = DiscoveryTestBuilder.MakeTable("sales", "inactive");
        inactive = inactive with { Table = inactive.Table with { IsActive = false } };
        var all = new List<TableMetadata> { visible, hidden, foreignVirtual, inactive };

        // pageSize 500 = one page (equivalent to the former single full read); pageSize 1 = one table per page.
        var onePage = new CountingCatalogRepository(all, pageSize: 500);
        var perRow = new CountingCatalogRepository(all, pageSize: 1);

        var fromOnePage = await CreateDiscovery(onePage).ListAsync(new DiscoveryQuery(), CallerOf());
        var fromRows = await CreateDiscovery(perRow).ListAsync(new DiscoveryQuery(), CallerOf());

        var expected = fromOnePage.Items.Select(Id).OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();
        fromRows.Items.Select(Id).OrderBy(t => t.ToString(), StringComparer.Ordinal).ShouldBe(expected);
        fromRows.TotalCount.ShouldBe(fromOnePage.TotalCount);
        fromRows.Items.ShouldNotContain(i => Id(i) == hidden.Identifier);
        fromRows.Items.ShouldNotContain(i => Id(i) == foreignVirtual.Identifier);
        fromRows.Items.ShouldNotContain(i => Id(i) == inactive.Identifier);
        fromRows.Items.ShouldContain(i => Id(i) == visible.Identifier);
        perRow.QueryCalls.Count.ShouldBeGreaterThan(onePage.QueryCalls.Count);
    }

    private static TableIdentifier Id(DiscoveryObject o) => new(o.Domain, o.Schema, o.Name);

    private static DiscoveryService CreateDiscovery(ITableMetadataRepository catalog) => new(
        catalog,
        new FakeAccessDecisionPoint(req => TableAccessDecision.Allowed(
            req.Object.Identifier,
            req.Object.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase))),
        new FakeDiscoveryConsentRepository(),
        new FakeDiscoveryApprovalRepository(),
        new FakeDiscoveryOwnershipRepository(),
        new FakeDiscoveryArtifactStore(),
        new FakeAuditWriter(),
        Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
        Options.Create(new CatalogDiscoveryOptions()));

    private static CatalogCaller CallerOf() => DiscoveryTestBuilder.Caller(
        DiscoveryTestBuilder.User("S-1-5-21-100-1", TenantA.Value), TenantA);

    /// <summary>Counting catalog double: pages of <c>pageSize</c> rows, records every read shape.</summary>
    internal sealed class CountingCatalogRepository(IReadOnlyList<TableMetadata> tables, int pageSize) : ITableMetadataRepository
    {
        public int GetAllCalls { get; private set; }

        public List<CatalogTableQuery> QueryCalls { get; } = [];

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult(tables);
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            QueryCalls.Add(query);
            query.EnsureValid(out _, out _, out _, out var after);
            var matched = tables
                .Where(t => query.IncludeInactive || t.Table.IsActive)
                .Where(t => after is null || CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
                .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                .Take(pageSize + 1)
                .ToList();
            var hasMore = matched.Count > pageSize;
            var items = hasMore ? matched.Take(pageSize).ToList() : matched;
            return Task.FromResult(new CatalogTablePage(items, hasMore ? CatalogTableCursor.Encode(items[^1].Identifier) : null));
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<long> FlushCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(1L);
    }
}
