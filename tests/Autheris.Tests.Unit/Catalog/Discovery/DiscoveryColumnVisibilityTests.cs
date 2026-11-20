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
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.GraphQL.Types;
using Shouldly;
using Xunit;

/// <summary>
/// A column the caller has no access to is never named and never counted on MCP get_table_schema, get_my_permissions,
/// describe_dataset, list_datasets and GraphQL catalog discovery.
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
        new(repo, new FakeDiscoveryConsentRepository(), new UnusedGatewayExecution(), discoveryService: discovery);

    private static McpToolExecutionHandler Handler(CatalogDatasetDetail? detail)
    {
        return new McpToolExecutionHandler(
            new UnusedSqlExecutionService(),
            new UnusedDataQueryService(),
            new DetailCatalogDiscoveryService(detail),
            new UnusedApiDispatcherService(),
            new UnusedLineageGraphStore(),
            new EmptyTableMetadataRepository());
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
        detail.Columns.ShouldBeEmpty("an ungranted table lists no column on the REST detail either");

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
    public async Task RestDetail_UngrantedTable_ListsNoColumns()
    {
        var (discovery, _) = Create(Mode.NoConsent);

        var detail = DetailOf(await ObjectAsync(discovery, Patients));

        detail.Columns.ShouldBeEmpty();
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

    private static IConsentResolutionService Resolver() => new FixedLevelsResolver();

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

    private sealed class FixedLevelsResolver : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid, IReadOnlySet<Sid> subjectGroupSids, IReadOnlySet<string> userRoles, TableIdentifier table,
            IReadOnlyList<Consent> activeConsents, DatabaseDialect dialect, IReadOnlySet<Sid>? allUserSids, IReadOnlySet<string>? callerScopes) =>
            TableAccessDecision.Allowed(
                table,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["fullname"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask,
                    [Denied] = ColumnAccessLevel.Deny
                });
    }

    private sealed class UnusedGatewayExecution : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class DetailCatalogDiscoveryService(CatalogDatasetDetail? detail) : ICatalogDiscoveryService
    {
        public Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscoveryFacets> ListFacetsAsync(RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default) => Task.FromResult(detail);
        public Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogSearchHit>> SearchCatalogDetailedAsync(CatalogSearchQuery query, RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogDatasourceSummary>> ListDatasourcesAsync(RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceRegistrationResult> RegisterDatasourceAsync(DatasourceRegistrationRequest request, RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedSqlExecutionService : IGovernedSqlExecutionService
    {
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedDataQueryService : IGovernedDataQueryService
    {
        public Task<DatasetQueryEnvelope> ExecuteQueryAsync(DatasetQueryRequest request, RequestContext context, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedApiDispatcherService : IApiDispatcherService
    {
        public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> InvokeApiAsync(string endpoint, string method, IReadOnlyDictionary<string, object?>? parameters = null, object? body = null, RequestContext? context = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetOpenApiJsonAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedLineageGraphStore : ILineageGraphStore
    {
        public int Count => 0;
        public LineageNode? GetNode(string nodeId) => null;
        public bool ContainsNode(string nodeId) => false;
        public void UpdateGraph(IEnumerable<LineageNode> nodes) => throw new NotSupportedException();
        public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
    }

    private sealed class EmptyTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}
