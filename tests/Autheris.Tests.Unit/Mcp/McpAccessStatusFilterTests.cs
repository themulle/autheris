namespace Autheris.Tests.Unit.Mcp;

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
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Unit.Catalog.Discovery;
using IRequestExecutorProvider = HotChocolate.Execution.IRequestExecutorProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issues #798 / #804: accessStatus on search_catalog / find_relevant_tables and the accessStatus filter of the MCP catalog tools.</summary>
public sealed class McpAccessStatusFilterTests
{
    private static readonly McpSessionContext Session = new(
        "s1",
        "agent",
        "tenant-a",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        UserSid: "S-1-USER");

    private static McpToolDefinition Tool(string name) => McpDatasetTools.Definitions().Single(t => t.Name == name);

    private static CatalogSearchHit Hit(string table, string? accessStatus) => new(
        TableIdentifier: new TableIdentifier("sales", "dbo", table),
        DisplayName: table,
        Description: "d",
        Domain: "sales",
        Sensitivity: "NORMAL",
        CombinedScore: 0.9,
        Bm25Score: 0.9,
        VectorScore: null,
        MatchedTerms: ["orders"],
        RelevantColumns: [],
        RelatedJoinPaths: [],
        SuggestedGraphQlField: null,
        AccessStatus: accessStatus);

    private static (McpToolExecutionHandler Handler, ICatalogDiscoveryService Search) Handler(params CatalogSearchHit[] hits)
    {
        var search = Substitute.For<ICatalogDiscoveryService>();
        search.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>(hits));
        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            search,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<ITableMetadataRepository>());
        return (handler, search);
    }

    [Fact]
    public async Task SearchCatalog_EveryHitCarriesAccessStatus()
    {
        var (handler, _) = Handler(Hit("orders", "GRANTED"), Hit("invoices", "REQUEST_ACCESS"));

        var json = await handler.ExecuteToolAsync(Tool(McpDatasetTools.SearchCatalog), """{"query":"orders"}""", Session, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var datasets = doc.RootElement.GetProperty("datasets").EnumerateArray().ToList();
        datasets.Select(d => d.GetProperty("accessStatus").GetString()).ShouldBe(["GRANTED", "REQUEST_ACCESS"]);
    }

    [Theory]
    [InlineData(McpDatasetTools.SearchCatalog, """{"query":"orders","accessStatus":"GRANTED"}""", DiscoveryAccessState.Granted)]
    [InlineData(McpDatasetTools.SearchCatalog, """{"query":"orders","accessStatus":"REQUEST_ACCESS"}""", DiscoveryAccessState.None)]
    [InlineData(McpDatasetTools.FindRelevantTables, """{"use_case":"orders","accessStatus":"PENDING"}""", DiscoveryAccessState.Requested)]
    [InlineData(McpDatasetTools.FindRelevantTables, """{"use_case":"orders","accessStatus":"EXPIRED"}""", DiscoveryAccessState.Expired)]
    public async Task SearchTools_PassTheFilterToTheSearch(string tool, string args, DiscoveryAccessState expected)
    {
        var (handler, search) = Handler(Hit("orders", "GRANTED"));

        await handler.ExecuteToolAsync(Tool(tool), args, Session, CancellationToken.None);

        await search.Received(1).SearchCatalogDetailedAsync(
            Arg.Is<CatalogSearchQuery>(q => q.AccessStatus == expected),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchTools_WithoutFilter_PassNoFilter()
    {
        var (handler, search) = Handler(Hit("orders", "GRANTED"));

        await handler.ExecuteToolAsync(Tool(McpDatasetTools.FindRelevantTables), """{"use_case":"orders"}""", Session, CancellationToken.None);

        await search.Received(1).SearchCatalogDetailedAsync(
            Arg.Is<CatalogSearchQuery>(q => q.AccessStatus == null), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(McpDatasetTools.SearchCatalog, "query", "\"READABLE<script>\"")]
    [InlineData(McpDatasetTools.FindRelevantTables, "use_case", "42")]
    public async Task SearchTools_InvalidFilter_IsRejectedWithoutSearchingOrEchoing(string tool, string textArgument, string accessStatusJson)
    {
        var (handler, search) = Handler(Hit("orders", "GRANTED"));
        var args = "{\"" + textArgument + "\":\"orders\",\"accessStatus\":" + accessStatusJson + "}";

        var json = await handler.ExecuteToolAsync(Tool(tool), args, Session, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("isError").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("REQUEST_ACCESS");
        json.ShouldNotContain("READABLE");
        await search.DidNotReceiveWithAnyArgs().SearchCatalogDetailedAsync(default!, default!, default);
    }

    [Fact]
    public async Task FindRelevantTables_CandidatesCarryAccessStatus_AndTheNextStepAsksForAccess()
    {
        var (handler, _) = Handler(Hit("orders", "GRANTED"), Hit("invoices", "REQUEST_ACCESS"));

        var json = await handler.ExecuteToolAsync(Tool(McpDatasetTools.FindRelevantTables), """{"use_case":"orders"}""", Session, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("accessStatus").GetString())
            .ShouldBe(["GRANTED", "REQUEST_ACCESS"]);
        var next = doc.RootElement.GetProperty("recommendedNextStep").GetString()!;
        next.ShouldContain("accessStatus GRANTED");
        next.ShouldContain("Request access");
    }

    [Fact]
    public void RecommendedNextStep_NamesQueryToolsOnlyForReadableCandidates()
    {
        static McpUseCaseCandidate C(string? status) => new("sales.dbo.t", "sales", null, 1, [], [], status);

        McpToolExecutionHandler.RecommendedNextStepFor([C("GRANTED"), C("GRANTED")]).ShouldContain("query_sql");
        McpToolExecutionHandler.RecommendedNextStepFor([]).ShouldContain("get_table_schema");

        var none = McpToolExecutionHandler.RecommendedNextStepFor([C("REQUEST_ACCESS"), C("PENDING")]);
        none.ShouldNotContain("query_sql");
        none.ShouldNotContain("get_table_schema");
        none.ShouldContain("request access");
    }

    [Theory]
    [InlineData(McpDatasetTools.ListDatasets)]
    [InlineData(McpDatasetTools.SearchCatalog)]
    [InlineData(McpDatasetTools.FindRelevantTables)]
    public void ToolSchemas_DeclareTheAccessStatusEnum(string name)
    {
        using var schema = JsonDocument.Parse(Tool(name).InputJsonSchema);
        var prop = schema.RootElement.GetProperty("properties").GetProperty("accessStatus");
        prop.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["GRANTED", "PENDING", "EXPIRED", "REQUEST_ACCESS"]);

        var required = name switch
        {
            McpDatasetTools.SearchCatalog => ""","query":"orders" """,
            McpDatasetTools.FindRelevantTables => ""","use_case":"orders" """,
            _ => string.Empty
        };
        var validator = new PersistedToolValidator();
        using var ok = JsonDocument.Parse("{\"accessStatus\":\"GRANTED\"" + required + "}");
        validator.ValidateToolInvocation(Tool(name), ok.RootElement, out _).ShouldBeTrue();

        using var bad = JsonDocument.Parse("{\"accessStatus\":\"EVERYTHING\"" + required + "}");
        validator.ValidateToolInvocation(Tool(name), bad.RootElement, out _).ShouldBeFalse();
    }

    // list_datasets (McpDatasetCatalog)

    private static McpDatasetCatalog DiscoveryCatalog(int tables, params int[] granted)
    {
        var repo = new FakeDiscoveryTableRepository();
        for (var i = 0; i < tables; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", $"t{i:00}"));
        }

        var consents = new FakeDiscoveryConsentRepository();
        foreach (var i in granted)
        {
            consents.Active.Add(new Consent
            {
                TableIdentifier = repo.Tables[i].Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-A"),
                TenantId = new TenantId("tenant-a"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30)
            });
        }

        var grantedIds = granted.Select(i => repo.Tables[i].Identifier).ToHashSet();
        var pdp = new FakeAccessDecisionPoint(r => grantedIds.Contains(r.Object.Identifier)
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied"));
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp, consents: consents);
        return new McpDatasetCatalog(repo, consents, Substitute.For<IGatewayExecutionService>(), discoveryService: discovery);
    }

    [Fact]
    public async Task ListDatasets_Granted_TotalAndNextOffsetReferToTheFilteredSet()
    {
        var catalog = DiscoveryCatalog(12, 3, 7, 11);
        var principal = DiscoveryTestBuilder.User("S-A", "tenant-a");

        var first = await catalog.ListDatasetsAsync(principal, null, null, offset: 0, limit: 2, null, null, null, null, accessStatus: "GRANTED");
        var last = await catalog.ListDatasetsAsync(principal, null, null, offset: 2, limit: 2, null, null, null, null, accessStatus: "GRANTED");

        first.Total.ShouldBe(3);
        first.NextOffset.ShouldBe(2);
        first.Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.t03", "sales.dbo.t07"]);
        last.NextOffset.ShouldBeNull();
        last.Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.t11"]);
        first.Datasets.Concat(last.Datasets).ShouldAllBe(d => d.AccessStatus == "GRANTED");
    }

    [Fact]
    public async Task ListDatasets_RequestAccess_ReturnsTheRest_WithTheSameStatusAsTheUnfilteredList()
    {
        var catalog = DiscoveryCatalog(5, 0);
        var principal = DiscoveryTestBuilder.User("S-A", "tenant-a");

        var all = await catalog.ListDatasetsAsync(principal, null, null, 0, 50, null, null, null, null, accessStatus: null);
        var rest = await catalog.ListDatasetsAsync(principal, null, null, 0, 50, null, null, null, null, accessStatus: "REQUEST_ACCESS");

        rest.Total.ShouldBe(4);
        rest.Datasets.Select(d => d.Id).ShouldBe(all.Datasets.Where(d => d.AccessStatus == "REQUEST_ACCESS").Select(d => d.Id));
    }

    [Fact]
    public async Task ListDatasets_Overview_CountsTheFilteredSet()
    {
        var catalog = DiscoveryCatalog(6, 1, 2);
        var principal = DiscoveryTestBuilder.User("S-A", "tenant-a");

        var overview = await catalog.ListDatasetsAsync(principal, null, null, null, null, null, null, overview: true, null, accessStatus: "GRANTED");

        overview.Total.ShouldBe(2);
    }

    [Fact]
    public async Task ListDatasets_InvalidFilter_Throws()
    {
        var catalog = DiscoveryCatalog(2, 0);

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            catalog.ListDatasetsAsync(DiscoveryTestBuilder.User("S-A", "tenant-a"), null, null, 0, 10, null, null, null, null, accessStatus: "MINE"));
        ex.Message.ShouldStartWith(CatalogAccessStatus.InvalidFilterMessage);
    }

    [Fact]
    public async Task ListDatasets_LegacyPathWithAuthBypass_ReportsEverythingGranted()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "a"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "b"));
        var options = Options.Create(new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } });
        var catalog = new McpDatasetCatalog(repo, new FakeDiscoveryConsentRepository(), Substitute.For<IGatewayExecutionService>(), options: options);
        var principal = DiscoveryTestBuilder.User("S-A", "tenant-a");

        var granted = await catalog.ListDatasetsAsync(principal, null, null, 0, 1, null, null, null, null, accessStatus: "GRANTED");
        var requestable = await catalog.ListDatasetsAsync(principal, null, null, 0, 10, null, null, null, null, accessStatus: "REQUEST_ACCESS");

        granted.Total.ShouldBe(2);
        granted.NextOffset.ShouldBe(1);
        requestable.Total.ShouldBe(0);
        requestable.Datasets.ShouldBeEmpty();
    }

    // list_datasets through the MCP executor (the closed-world schema check is stubbed out to reach the executor's own check).

    private static GatewayMcpQueryExecutor Executor(IMcpDatasetCatalog catalog)
    {
        var validator = Substitute.For<IPersistedToolValidator>();
        validator.ValidateToolInvocation(Arg.Any<McpToolDefinition>(), Arg.Any<JsonElement>(), out Arg.Any<string?>()).Returns(true);
        return new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(),
            Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            McpRowCapTests.Options(100),
            datasetCatalog: catalog,
            persistedToolValidator: validator);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"MINE<script>\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task ListDatasets_InvalidFilter_IsRejectedWithTheFixedMessage(string accessStatusJson)
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();

        var json = await Executor(catalog).ExecuteOperationAsync(
            Tool(McpDatasetTools.ListDatasets), "{\"accessStatus\":" + accessStatusJson + "}", Session);

        json.ShouldContain("INVALID_PARAMS");
        json.ShouldContain("Allowed values: GRANTED, PENDING, EXPIRED, REQUEST_ACCESS");
        json.ShouldNotContain("MINE");
        catalog.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ListDatasets_Filter_ReachesTheCatalogWithAllArguments()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.ListDatasetsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new McpDatasetList([], 0, false));

        await Executor(catalog).ExecuteOperationAsync(
            Tool(McpDatasetTools.ListDatasets), """{"accessStatus":"GRANTED","limit":5,"format":"compact"}""", Session);

        await catalog.Received(1).ListDatasetsAsync(Arg.Any<ClaimsPrincipal>(), null, null, null, 5, null, null, null, "compact", "GRANTED", Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(McpDatasetTools.SearchCatalog, "query")]
    [InlineData(McpDatasetTools.FindRelevantTables, "use_case")]
    public async Task SearchTools_BlankFilter_IsRejected(string tool, string textArgument)
    {
        var (handler, search) = Handler(Hit("orders", "GRANTED"));

        var json = await handler.ExecuteToolAsync(Tool(tool), "{\"" + textArgument + "\":\"orders\",\"accessStatus\":\"\"}", Session, CancellationToken.None);

        json.ShouldContain("INVALID_PARAMS");
        await search.DidNotReceiveWithAnyArgs().SearchCatalogDetailedAsync(default!, default!, default);
    }
}
