namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// A column the caller has no access to is never named and never counted on MCP get_table_schema, get_my_permissions,
/// describe_dataset, list_datasets and GraphQL catalog discovery. The REST dataset detail keeps its request-access listing.
/// </summary>
[Trait("Category", "Security")]
public sealed class DiscoveryColumnVisibilityTests
{
    private const string Denied = "diagnosis_code";
    private const string DatasetId = "crm.dbo.patients";
    private static readonly TableIdentifier Patients = new("crm", "dbo", "patients");
    private static readonly TableIdentifier Vault = new("crm", "dbo", "vault");
    private static readonly McpSessionContext Session = new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

    private enum Mode { DenyOneColumn, NoConsent, ClearAll }

    private static TableMetadata Table(string name) => DiscoveryTestBuilder.MakeTable(
        "crm", name, columns: [("id", false, false), ("fullname", false, false), ("email", false, false), (Denied, false, false)]);

    private static FakeAccessDecisionPoint Pdp(Mode mode) => new(r =>
    {
        if (mode == Mode.NoConsent)
        {
            return TableAccessDecision.Denied(r.Object.Identifier, "no consent");
        }

        var levels = r.Object.Columns.ToDictionary(
            c => c.ColumnName,
            c => c.ColumnName switch
            {
                _ when r.Object.Identifier.TableName == "vault" => ColumnAccessLevel.Deny,
                Denied when mode == Mode.DenyOneColumn => ColumnAccessLevel.Deny,
                "email" => ColumnAccessLevel.Mask,
                _ => ColumnAccessLevel.Clear
            },
            StringComparer.OrdinalIgnoreCase);
        return TableAccessDecision.Allowed(r.Object.Identifier, levels);
    });

    private static (DiscoveryService Discovery, FakeDiscoveryTableRepository Repo) Create(Mode mode)
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(Table("patients"));
        repo.Tables.Add(Table("vault"));
        return (DiscoveryTestBuilder.CreateService(tables: repo, pdp: Pdp(mode)), repo);
    }

    private static ClaimsPrincipal User(params string[] roles) => DiscoveryTestBuilder.User("S-1-USER", "tenant-a", roles);

    private static CatalogCaller Caller(ClaimsPrincipal principal, CatalogChannel channel = CatalogChannel.Mcp) =>
        DiscoveryTestBuilder.Caller(principal, channel: channel);

    private static async Task<DiscoveryObject> ObjectAsync(DiscoveryService discovery, TableIdentifier id, ClaimsPrincipal? principal = null) =>
        (await discovery.GetAsync(id, Caller(principal ?? User()), CancellationToken.None))!;

    private static McpDatasetCatalog CatalogOf(DiscoveryService discovery, FakeDiscoveryTableRepository repo) =>
        new(repo, new FakeDiscoveryConsentRepository(), Substitute.For<IGatewayExecutionService>(), discoveryService: discovery);

    private static McpToolExecutionHandler Handler(CatalogDatasetDetail? detail)
    {
        var catalog = Substitute.For<ICatalogDiscoveryService>();
        catalog.GetDatasetDetailAsync(Arg.Any<TableIdentifier>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(detail));
        return new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            catalog,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<ITableMetadataRepository>());
    }

    private static async Task<JsonElement> RunAsync(McpToolExecutionHandler handler, string tool, string arguments)
    {
        var json = await handler.ExecuteToolAsync(new McpToolDefinition(tool, tool, "{}", TargetGraphQLOperation: tool), arguments, Session);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IReadOnlyList<string> SchemaColumns(JsonElement result) =>
        result.GetProperty("tables")[0].GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();

    private static CatalogDatasetDetail DetailOf(DiscoveryObject obj) => CatalogDiscoveryProjection.ToDatasetDetail(obj, null);

    [Fact]
    public async Task GetTableSchema_NoAccessColumn_IsNotListed()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);
        var detail = DetailOf(await ObjectAsync(discovery, Patients, User()));

        var result = await RunAsync(Handler(detail), McpDatasetTools.GetTableSchema, $$"""{"dataset":"{{DatasetId}}"}""");

        SchemaColumns(result).ShouldBe(["id", "fullname", "email"], ignoreOrder: true);
        result.GetRawText().ShouldNotContain(Denied);
    }

    [Fact]
    public async Task GetTableSchema_UngrantedTable_ListsNoColumns()
    {
        var (discovery, _) = Create(Mode.NoConsent);
        var detail = DetailOf(await ObjectAsync(discovery, Patients));
        detail.Columns.ShouldNotBeEmpty("the REST detail keeps the request-access listing");

        var result = await RunAsync(Handler(detail), McpDatasetTools.GetTableSchema, $$"""{"dataset":"{{DatasetId}}"}""");

        SchemaColumns(result).ShouldBeEmpty();
        result.GetRawText().ShouldNotContain(Denied);
        result.GetRawText().ShouldNotContain("fullname");
    }

    [Fact]
    public async Task GetTableSchema_And_DescribeDataset_ReturnSameColumnSet()
    {
        var (discovery, repo) = Create(Mode.DenyOneColumn);
        var detail = DetailOf(await ObjectAsync(discovery, Patients));
        var described = await CatalogOf(discovery, repo).DescribeDatasetAsync(User(), DatasetId, language: null, brief: false);

        var schema = await RunAsync(Handler(detail), McpDatasetTools.GetTableSchema, $$"""{"dataset":"{{DatasetId}}"}""");

        SchemaColumns(schema).ShouldBe(described.Columns.Select(c => c.Name), ignoreOrder: true);
        described.Columns.ShouldNotContain(c => c.Name == Denied);
    }

    [Fact]
    public async Task GetMyPermissions_NoAccessColumn_IsNotListed()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);
        var detail = DetailOf(await ObjectAsync(discovery, Patients));

        var result = await RunAsync(Handler(detail), McpDatasetTools.GetMyPermissions, $$"""{"dataset":"{{DatasetId}}"}""");

        var names = result.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        names.ShouldBe(["id", "fullname", "email"], ignoreOrder: true);
        result.GetRawText().ShouldNotContain(Denied);
    }

    [Fact]
    public async Task GetMyPermissions_UngrantedTable_ListsNoColumns()
    {
        var (discovery, _) = Create(Mode.NoConsent);
        var detail = DetailOf(await ObjectAsync(discovery, Patients));

        var result = await RunAsync(Handler(detail), McpDatasetTools.GetMyPermissions, $$"""{"dataset":"{{DatasetId}}"}""");

        result.GetProperty("columns").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task GetTableSchema_GrantedColumn_ListedWithClearOrMaskAccess()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);
        var detail = DetailOf(await ObjectAsync(discovery, Patients));

        var result = await RunAsync(Handler(detail), McpDatasetTools.GetTableSchema, $$"""{"dataset":"{{DatasetId}}"}""");

        var access = result.GetProperty("tables")[0].GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("access").GetString());
        access["id"].ShouldBe("clear");
        access["email"].ShouldBe("mask");
    }

    [Fact]
    public async Task ListDatasets_ColumnCount_ExcludesNoAccessColumns()
    {
        var (discovery, repo) = Create(Mode.DenyOneColumn);

        var list = await CatalogOf(discovery, repo).ListDatasetsAsync(User(), null, null, CancellationToken.None);

        list.Datasets.Single(d => d.Id == DatasetId).ColumnCount.ShouldBe(3);
        list.Datasets.Single(d => d.Id == "crm.dbo.vault").ColumnCount.ShouldBe(0);
    }

    [Fact]
    public async Task GraphQlDiscovery_DeniedColumn_IsNotListed()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);

        var page = await discovery.ListAsync(
            new DiscoveryQuery(null, null, null, null, null, 100, 0, DiscoverySurface.GraphQl),
            Caller(User(), CatalogChannel.GraphQl),
            CancellationToken.None);
        var dto = page.Items.Select(o => Query.ToTableMetadataDto(o)).Single(t => t.TableName == "patients");

        dto.Columns.ShouldNotContain(c => c.Name == Denied);
        dto.ColumnNames.ShouldNotContain(Denied);
    }

    [Fact]
    public async Task GraphQlCatalogObject_DeniedColumn_IsNotListed()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);

        var dto = Query.ToTableMetadataDto(await ObjectAsync(discovery, Patients));

        dto.Columns.Select(c => c.Name).ShouldBe(["id", "fullname", "email"], ignoreOrder: true);
        dto.ColumnNames.ShouldBe(["id", "fullname", "email"], ignoreOrder: true);
    }

    [Fact]
    public async Task GraphQlDiscovery_GrantedColumn_StillListedWithState()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);

        var dto = Query.ToTableMetadataDto(await ObjectAsync(discovery, Patients));

        dto.Columns.Single(c => c.Name == "id").State.ShouldBe(DiscoveryColumnState.Clear);
        dto.Columns.Single(c => c.Name == "email").State.ShouldBe(DiscoveryColumnState.Masked);
    }

    [Fact]
    public async Task DiscoveryColumnVisibility_AdminView_StillFiltersNoAccess()
    {
        var (discovery, _) = Create(Mode.DenyOneColumn);

        var obj = await ObjectAsync(discovery, Patients, User("GovernanceAdmin"));

        DiscoveryColumnVisibility.VisibleColumns(obj).ShouldNotContain(c => c.Name == Denied);
        Query.ToTableMetadataDto(obj).ColumnNames.ShouldNotContain(Denied);
    }

    [Fact]
    public async Task DiscoveryColumnVisibility_UnresolvedColumns_FailsClosed()
    {
        var (discovery, _) = Create(Mode.ClearAll);
        var obj = await ObjectAsync(discovery, Patients);

        DiscoveryColumnVisibility.VisibleColumns(obj with { Columns = null! }).ShouldBeEmpty();
        DiscoveryColumnVisibility.VisibleColumns((DiscoveryObject?)null).ShouldBeEmpty();
        DiscoveryColumnVisibility.VisibleCount(obj with { Columns = [] }).ShouldBe(0);

        var unknown = obj with { Columns = [obj.Columns[0] with { State = (DiscoveryColumnState)99 }, obj.Columns[1]] };
        DiscoveryColumnVisibility.VisibleColumns(unknown).Select(c => c.Name).ShouldBe([obj.Columns[1].Name]);

        var detail = DetailOf(obj);
        DiscoveryColumnVisibility.VisibleColumns(detail with { Columns = null! }).ShouldBeEmpty();
        DiscoveryColumnVisibility.VisibleColumns(detail with { Columns = [detail.Columns[0] with { MaskingState = "" }] }).ShouldBeEmpty();
        Query.ToTableMetadataDto(obj with { Columns = null! }).ColumnNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task RestDetail_UngrantedTable_StillListsDenyColumns()
    {
        var (discovery, _) = Create(Mode.NoConsent);

        var detail = DetailOf(await ObjectAsync(discovery, Patients));

        detail.Columns.Select(c => c.Name).ShouldContain(Denied);
        detail.Columns.ShouldAllBe(c => c.MaskingState == "Deny");
    }

    [Fact]
    public async Task DiscoveryColumnVisibility_UnknownMaskingState_IsNotListed()
    {
        var (discovery, _) = Create(Mode.ClearAll);
        var detail = DetailOf(await ObjectAsync(discovery, Patients));
        var column = detail.Columns[0];

        DiscoveryColumnVisibility.VisibleColumns(detail with { Columns = [column with { MaskingState = "SomethingNew" }] }).ShouldBeEmpty();
        DiscoveryColumnVisibility.VisibleColumns(detail with { Columns = [column with { MaskingState = "DENY" }] }).ShouldBeEmpty();
        DiscoveryColumnVisibility.VisibleColumns(detail with { Columns = [column with { MaskingState = "HMAC_SHA256" }] }).Count.ShouldBe(1);
    }

    private static IConsentResolutionService Resolver()
    {
        var resolver = Substitute.For<IConsentResolutionService>();
        resolver.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<Autheris.Domain.Common.DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(call => TableAccessDecision.Allowed(
                call.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["fullname"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask,
                    [Denied] = ColumnAccessLevel.Deny
                }));
        return resolver;
    }

    [Fact]
    public void GraphQlCatalog_DeniedColumn_IsNotListed()
    {
        var dto = Query.ToCatalogDto(Table("patients") with { CatalogView = CatalogView.Granted }, isAdminView: false, status: "GRANTED", principal: User(), consentResolution: Resolver(), activeConsents: []);

        dto!.ColumnNames.ShouldBe(["id", "fullname", "email"], ignoreOrder: true);
        dto.Columns.Select(c => c.Name).ShouldNotContain(Denied);
        dto.Columns.Single(c => c.Name == "email").State.ShouldBe(DiscoveryColumnState.Masked);
    }

    [Theory]
    [InlineData("REQUEST_ACCESS")]
    [InlineData("PENDING")]
    [InlineData("EXPIRED")]
    public void GraphQlCatalog_UngrantedTable_ListsNoColumns(string status)
    {
        var dto = Query.ToCatalogDto(Table("patients") with { CatalogView = CatalogView.Granted }, isAdminView: false, status: status, principal: User(), consentResolution: Resolver(), activeConsents: []);

        dto!.Columns.ShouldBeEmpty();
        dto.ColumnNames.ShouldBeEmpty();
    }

    [Fact]
    public void GraphQlCatalog_UnresolvableCaller_ListsNoColumns()
    {
        var dto = Query.ToCatalogDto(Table("patients") with { CatalogView = CatalogView.Granted }, isAdminView: false, status: "GRANTED");

        dto!.Columns.ShouldBeEmpty();
        dto.ColumnNames.ShouldBeEmpty();
    }
}
