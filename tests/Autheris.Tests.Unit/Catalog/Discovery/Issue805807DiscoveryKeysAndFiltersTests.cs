namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issues #805 and #807: on the discovery-service path (the production path since v1.8.0) MCP <c>describe_dataset</c> and the REST
/// catalog detail report the declared primary key (order and direction), the declared indexes and the required filter columns, under
/// the same caller visibility rule as before (SEC-805-1..3, SEC-807-1).
/// </summary>
public sealed class Issue805807DiscoveryKeysAndFiltersTests
{
    private static readonly TableIdentifier Readings = new("sensors", "dbo", "readings");

    private static TableMetadata ReadingsTable(
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<KeyColumnDirection>? pkDirections = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        ImmutableArray<string>? requiredFilterColumns = null,
        ImmutableArray<ImmutableArray<string>>? requiredFilterColumnGroups = null)
    {
        var baseTable = DiscoveryTestBuilder.MakeTable("sensors", "readings", columns:
        [
            ("device", false, false),
            ("ts", false, false),
            ("reading", false, false),
            ("site", false, false),
            ("secret", true, false),
            ("internal_ref", false, true)
        ]);
        return new TableMetadata
        {
            Identifier = baseTable.Identifier,
            Table = new Table
            {
                Id = baseTable.Table.Id,
                SourceName = baseTable.Table.SourceName,
                SchemaName = baseTable.Table.SchemaName,
                TableName = baseTable.Table.TableName,
                DisplayName = baseTable.Table.DisplayName,
                Description = baseTable.Table.Description,
                DataSourceType = baseTable.Table.DataSourceType,
                IsActive = true,
                Sensitivity = baseTable.Table.Sensitivity,
                RequiredFilterColumns = requiredFilterColumns ?? ["device"],
                RequiredFilterColumnGroups = requiredFilterColumnGroups ?? [["ts"], ["reading", "site"]]
            },
            Columns = baseTable.Columns,
            OwnerTenantId = baseTable.OwnerTenantId,
            CatalogTags = baseTable.CatalogTags,
            PrimaryKeyColumns = pk ?? ["device", "ts"],
            PrimaryKeyDirections = pkDirections ?? [KeyColumnDirection.Ascending, KeyColumnDirection.Descending],
            Indexes = indexes ??
            [
                new TableIndexDefinition("ix_readings_ts", ["ts"], false, [KeyColumnDirection.Descending]),
                new TableIndexDefinition("ix_readings_secret", ["secret"], false)
            ]
        };
    }

    private static FakeAccessDecisionPoint Pdp(Dictionary<string, ColumnAccessLevel>? columnAccess = null) => new(r =>
        TableAccessDecision.Allowed(r.Object.Identifier, columnAccess ?? new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

    private static (McpDatasetCatalog Mcp, CatalogDiscoveryService RestService, RequestContext Context, System.Security.Claims.ClaimsPrincipal User) Build(
        TableMetadata table, FakeAccessDecisionPoint pdp)
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(table);
        var audit = new FakeAuditWriter();
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp, audit: audit);
        var user = DiscoveryTestBuilder.User("S-1-USER", "tenant-a");
        var mcp = new McpDatasetCatalog(repo, new FakeDiscoveryConsentRepository(), Substitute.For<IGatewayExecutionService>(), discoveryService: discovery);
        var rest = new CatalogDiscoveryService(
            repo,
            audit,
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            logger: NullLogger<CatalogDiscoveryService>.Instance,
            discoveryService: discovery);
        return (mcp, rest, new RequestContext(user, new TenantId("tenant-a")), user);
    }

    [Fact]
    public async Task DescribeDataset_ReportsCompositeKeyWithOrderAndDirection_AndSecondaryIndexWithDirection()
    {
        var (mcp, _, _, user) = Build(ReadingsTable(), Pdp());

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());

        described.PrimaryKeyOrder.ShouldNotBeNull();
        described.PrimaryKeyOrder.Select(k => (k.Column, k.Direction)).ShouldBe([("device", "ASC"), ("ts", "DESC")]);
        described.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ShouldBe(["device", "ts"]);
        described.Indexes.ShouldNotBeNull();
        var index = described.Indexes.ShouldHaveSingleItem();
        index.Columns.ShouldBe(["ts"]);
        index.Unique.ShouldBeFalse();
        index.Directions.ShouldBe([KeyColumnDirection.Descending]);
    }

    [Fact]
    public async Task DescribeDataset_IncludeKeysAndIndexes_KeepsThem()
    {
        var (mcp, _, _, user) = Build(ReadingsTable(), Pdp());
        McpDescribeNarrowing.TryParse("keys,indexes", null, null, null, null, out var request).ShouldBeTrue();

        var narrowed = McpDescribeNarrowing.Apply(await mcp.DescribeDatasetAsync(user, Readings.ToString()), request);

        narrowed.PrimaryKeyOrder.ShouldNotBeNull().Select(k => k.Direction).ShouldBe(["ASC", "DESC"]);
        narrowed.Indexes.ShouldNotBeNull().ShouldHaveSingleItem().Directions.ShouldBe([KeyColumnDirection.Descending]);
    }

    [Fact]
    public async Task DescribeDataset_ReportsRequiredFilterColumnsAndGroups()
    {
        var (mcp, _, _, user) = Build(ReadingsTable(), Pdp());

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());

        described.RequiredFilterColumns.ShouldBe(["device"]);
        described.RequiredFilterColumnGroups.ShouldNotBeNull();
        described.RequiredFilterColumnGroups.Select(g => string.Join(",", g)).ShouldBe(["ts", "reading,site"]);
    }

    [Fact]
    public async Task RestDetail_ReportsKeyOrderDirectionsIndexesAndRequiredFilters()
    {
        var (_, rest, context, _) = Build(ReadingsTable(), Pdp());

        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBe(["device", "ts"]);
        detail.PrimaryKeyDirections.ShouldBe(["ASC", "DESC"]);
        detail.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ShouldBe(["device", "ts"]);
        var index = detail.Indexes.ShouldNotBeNull().ShouldHaveSingleItem();
        index.Columns.ShouldBe(["ts"]);
        index.Directions.ShouldBe([KeyColumnDirection.Descending]);
        detail.RequiredFilterColumns.ShouldBe(["device"]);
        detail.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts", "reading,site"]);
    }

    [Fact]
    public async Task RestDetail_AllAscendingKey_HasNoDirectionList()
    {
        var (_, rest, context, _) = Build(ReadingsTable(pkDirections: []), Pdp());

        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBe(["device", "ts"]);
        detail.PrimaryKeyDirections.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MaskedKeyColumn_OmitsKeyAndItsIndex_OnBothSurfaces()
    {
        var (mcp, rest, context, user) = Build(ReadingsTable(), Pdp(new Dictionary<string, ColumnAccessLevel> { ["ts"] = ColumnAccessLevel.Mask }));

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());
        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        described.PrimaryKeyOrder.ShouldBeNull();
        described.Columns.ShouldAllBe(c => !c.PrimaryKey);
        (described.Indexes ?? []).ShouldBeEmpty();
        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBeEmpty();
        detail.PrimaryKeyDirections.ShouldBeNull();
        detail.Columns.ShouldAllBe(c => !c.IsPrimaryKey);
        (detail.Indexes ?? []).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DiscoveryHiddenKeyColumn_OmitsKey_AndHiddenRequiredFilterNamesAreWithheld()
    {
        var table = ReadingsTable(
            pk: ["device", "internal_ref"],
            pkDirections: [],
            indexes: [new TableIndexDefinition("ix_ref", ["internal_ref"], true)],
            requiredFilterColumns: ["device", "internal_ref"],
            requiredFilterColumnGroups: [["ts"], ["internal_ref", "site"]]);
        var (mcp, rest, context, user) = Build(table, Pdp());

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());
        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        described.PrimaryKeyOrder.ShouldBeNull();
        (described.Indexes ?? []).ShouldBeEmpty();
        described.RequiredFilterColumns.ShouldBe(["device"]);
        described.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBeEmpty();
        detail.RequiredFilterColumns.ShouldBe(["device"]);
        detail.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
        System.Text.Json.JsonSerializer.Serialize(described).ShouldNotContain("internal_ref");
        System.Text.Json.JsonSerializer.Serialize(detail).ShouldNotContain("internal_ref");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DeniedRequiredFilterColumn_IsNotNamed()
    {
        var (mcp, _, _, user) = Build(
            ReadingsTable(requiredFilterColumns: ["device", "reading"]),
            Pdp(new Dictionary<string, ColumnAccessLevel> { ["reading"] = ColumnAccessLevel.Deny }));

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());

        described.Columns.ShouldNotContain(c => c.Name == "reading");
        described.RequiredFilterColumns.ShouldBe(["device"]);
        described.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CallerWithoutAccess_GetsNoKeysIndexesOrRequiredFilters()
    {
        var (mcp, rest, context, user) = Build(ReadingsTable(), FakeAccessDecisionPoint.DenyAll());

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());
        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        described.PrimaryKeyOrder.ShouldBeNull();
        (described.Indexes ?? []).ShouldBeEmpty();
        described.RequiredFilterColumns.ShouldBeNull();
        described.RequiredFilterColumnGroups.ShouldBeNull();
        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBeEmpty();
        (detail.Indexes ?? []).ShouldBeEmpty();
        detail.RequiredFilterColumns.ShouldBeNull();
        detail.RequiredFilterColumnGroups.ShouldBeNull();
    }
    [Fact]
    [Trait("Category", "Security")]
    public async Task RowDependentKeyColumn_MapsToMasked_AndOmitsKeyAndIndex()
    {
        var pdp = new FakeAccessDecisionPoint(r =>
            TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                with { RowDependentColumns = new HashSet<string>(["ts"], StringComparer.OrdinalIgnoreCase) });
        var (mcp, rest, context, user) = Build(ReadingsTable(), pdp);

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());
        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        described.Columns.Single(c => c.Name == "ts").Access.ShouldBe("mask");
        described.PrimaryKeyOrder.ShouldBeNull();
        (described.Indexes ?? []).ShouldBeEmpty();
        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBeEmpty();
        (detail.Indexes ?? []).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MaskedRequiredFilterColumn_IsNamed_BecauseTheAnswerListsIt()
    {
        // Policy pin (SEC-807-1): the rule is "listed with access", not "Clear"; a masked column is listed, so naming it discloses nothing new.
        var (mcp, rest, context, user) = Build(
            ReadingsTable(requiredFilterColumns: ["device"]),
            Pdp(new Dictionary<string, ColumnAccessLevel> { ["device"] = ColumnAccessLevel.Mask }));

        var described = await mcp.DescribeDatasetAsync(user, Readings.ToString());
        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        described.Columns.Single(c => c.Name == "device").Access.ShouldBe("mask");
        described.RequiredFilterColumns.ShouldBe(["device"]);
        detail.ShouldNotBeNull().RequiredFilterColumns.ShouldBe(["device"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RestDetail_GroupWithDeniedMember_IsWithheld_AlthoughTheColumnIsListedAsDeny()
    {
        var (_, rest, context, _) = Build(
            ReadingsTable(requiredFilterColumns: ["device", "reading"]),
            Pdp(new Dictionary<string, ColumnAccessLevel> { ["reading"] = ColumnAccessLevel.Deny }));

        var detail = await rest.GetDatasetDetailAsync(Readings, context);

        detail.ShouldNotBeNull();
        detail.Columns.Single(c => c.Name == "reading").MaskingState.ShouldBe("Deny");
        detail.RequiredFilterColumns.ShouldBe(["device"]);
        detail.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
    }
}
