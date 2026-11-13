namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Profiling;
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
using Autheris.Tests.Unit.TestSupport;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Issue #702: MCP tool results carry no <c>null</c> members and no empty objects. The lists a client iterates stay
/// present, arrays are never omitted, row values stay untouched, and a column without known values has no value profile.
/// </summary>
public sealed class McpResultEmptyMembersTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    // Members that stay present even when null: clients index them without a presence check.
    private static readonly HashSet<string> AlwaysPresent = new(StringComparer.Ordinal) { "graphQlType", "domains" };

    private static readonly string[] ColumnNames =
        ["id", "customer_id", "order_date", "status", "amount", "currency", "region", "channel", "created_at", "updated_at"];

    private readonly ITableMetadataRepository _metadata = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly IGoldenQueryService _golden = Substitute.For<IGoldenQueryService>();
    private readonly IGraphQlCatalogMap _graphQl = Substitute.For<IGraphQlCatalogMap>();
    private readonly ColumnValueProfileService _profiles = new();

    private readonly ITestOutputHelper _output;

    public McpResultEmptyMembersTests(ITestOutputHelper output)
    {
        _output = output;
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([OrdersTable()]));
        _golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        // The caller holds an Allow consent with every column Clear, so describe_dataset runs the value-profile path.
        _consents.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(
            [
                new Consent
                {
                    TableIdentifier = Orders,
                    TenantId = new TenantId(Tenant),
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                    ColumnRules = ColumnNames.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
                }
            ]));
        // Every column but the last is in the GraphQL schema; the dataset has no relations.
        _graphQl.GetTableAsync(Orders, Arg.Any<CancellationToken>()).Returns(new GraphQlTableMapping(
            Orders, "sales_public_orders", "sales_public_orders", "sales_public_orders_filter", "sales_public_orders_order_by",
            ColumnNames.Take(ColumnNames.Length - 1).Select(c => new GraphQlColumnMapping(c, c, "String")).ToList(),
            []));
    }

    private static TableMetadata OrdersTable() => new()
    {
        Identifier = Orders,
        Table = new Table
        {
            TableName = Orders.TableName,
            SchemaName = Orders.Schema,
            Description = "Customer orders with amount, currency and sales channel, one row per order.",
            Sensitivity = "INTERNAL"
        },
        PrimaryKeyColumns = ["id"],
        Columns = ColumnNames
            .Select(c => new TableColumn { ColumnName = c, DataType = "varchar", Description = $"The {c.Replace('_', ' ')} of the order as recorded by the order system." })
            .ToList()
    };

    private McpDatasetCatalog Catalog()
    {
        var options = new GatewayOptions();
        return new McpDatasetCatalog(_metadata, _consents, Substitute.For<IGatewayExecutionService>(), _golden, Options.Create(options), _graphQl,
            profileService: _profiles,
            visibilityService: CatalogVisibilityTestFactory.Create(_metadata.ServeSingleReadsFromCatalog(), _consents, options),
            switches: ReadPathSwitches.From(options));
    }

    private static GatewayMcpQueryExecutor Executor(IMcpDatasetCatalog catalog) => new(
        Substitute.For<IRequestExecutorProvider>(), Substitute.For<IGatewayExecutionService>(), NullLogger<GatewayMcpQueryExecutor>.Instance,
        McpRowCapTests.Options(100), datasetCatalog: catalog);

    private static McpToolDefinition Tool(string name) =>
        McpDatasetTools.Definitions().FirstOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Tool '{name}' is not defined.");

    private static McpSessionContext Session() =>
        new("s1", "agent", Tenant, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

    /// <summary>Paths of members whose value is null or an object without members (array elements and dictionary entries of rows excluded by the caller).</summary>
    private static List<string> NullOrEmptyMembers(JsonElement element, string path = "$")
    {
        var found = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    var memberPath = $"{path}.{member.Name}";
                    var isEmptyObject = member.Value.ValueKind == JsonValueKind.Object && !member.Value.EnumerateObject().Any();
                    if ((member.Value.ValueKind == JsonValueKind.Null || isEmptyObject) && !AlwaysPresent.Contains(member.Name))
                    {
                        found.Add(memberPath);
                    }

                    found.AddRange(NullOrEmptyMembers(member.Value, memberPath));
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    found.AddRange(NullOrEmptyMembers(item, $"{path}[{i++}]"));
                }

                break;
        }

        return found;
    }

    [Fact]
    public async Task DescribeDataset_TenColumnsWithoutProfile_StaysWithinBudget_WithoutNullOrEmptyMembers()
    {
        var json = await Executor(Catalog()).ExecuteOperationAsync(Tool("describe_dataset"), """{"dataset":"sales.public.orders"}""", Session());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().ShouldBe("sales.public.orders");
        NullOrEmptyMembers(root).ShouldBeEmpty();

        // The lists a client iterates stay present, also when empty.
        var columns = root.GetProperty("columns");
        columns.GetArrayLength().ShouldBe(10);
        root.GetProperty("graphQl").GetProperty("relations").ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetProperty("examples").ValueKind.ShouldBe(JsonValueKind.Array);
        foreach (var column in columns.EnumerateArray())
        {
            column.TryGetProperty("graphQlType", out _).ShouldBeTrue();
        }

        columns[9].GetProperty("graphQlType").ValueKind.ShouldBe(JsonValueKind.Null);
        _output.WriteLine($"describe_dataset, 10 columns: {json.Length} characters");
        json.Length.ShouldBeLessThanOrEqualTo(4_500);
    }

    [Fact]
    public async Task DescribeDataset_ColumnWithoutKnownValues_HasNoValueProfile()
    {
        var description = await Catalog().DescribeDatasetAsync(User(), "sales.public.orders");

        description.Columns.ShouldAllBe(c => c.ValueProfile == null);
    }

    [Fact]
    public async Task DescribeDataset_ColumnWithStoredSnapshot_KeepsItsValueProfile()
    {
        _profiles.StoreProfileSnapshot(new ColumnProfileSnapshot(Orders, "status", EmptyShare: 0.0, TableRowCount: 1_000));

        var description = await Catalog().DescribeDatasetAsync(User(), "sales.public.orders");

        description.Columns.Single(c => c.Name == "status").ValueProfile.ShouldNotBeNull();
        description.Columns.Where(c => c.Name != "status").ShouldAllBe(c => c.ValueProfile == null);
    }

    [Fact]
    public async Task ListDatasets_EmptyResult_KeepsTheListsPresent()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.ListDatasetsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new McpDatasetList([], 0, false, Guidance: null, Endpoints: []));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("list_datasets"), "{}", Session());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("datasets").GetArrayLength().ShouldBe(0);
        root.GetProperty("endpoints").GetArrayLength().ShouldBe(0);
        root.TryGetProperty("domains", out _).ShouldBeTrue();
        root.TryGetProperty("guidance", out _).ShouldBeFalse();
        root.TryGetProperty("nextOffset", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SampleRows_NullCell_StaysNull()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.SampleRowsAsync(Arg.Any<ClaimsPrincipal>(), "sales.public.orders", Arg.Any<int?>(), false, Arg.Any<CancellationToken>())
            .Returns(new McpDatasetSample("sales.public.orders", 1, [new Dictionary<string, object?> { ["id"] = 1, ["note"] = null, ["meta"] = new Dictionary<string, object?>() }]));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"sales.public.orders"}""", Session());

        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("rows")[0];
        row.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("meta").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task QuerySql_NullCell_StaysNull_AndNullMembersAreOmitted()
    {
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("SELECT id, note FROM sales.public.orders", ["id", "note"],
                [new Dictionary<string, object?> { ["id"] = 1, ["note"] = null }], 1, 5));
        var handler = new McpToolExecutionHandler(
            sql, Substitute.For<IGovernedDataQueryService>(), Substitute.For<ICatalogDiscoveryService>(), Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(), _metadata);

        var json = await handler.ExecuteToolAsync(Tool(McpDatasetTools.QuerySql), """{"query":"SELECT id, note FROM sales.public.orders"}""", Session());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("rows")[0].GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("columns").GetArrayLength().ShouldBe(2);
        root.TryGetProperty("next", out _).ShouldBeFalse();
        root.TryGetProperty("page", out _).ShouldBeFalse();
        root.TryGetProperty("indexHints", out _).ShouldBeFalse();
    }

    private sealed class Node
    {
        public string? Name { get; set; }

        public Node? Next { get; set; }
    }

    [Fact]
    public void Options_OmitNestedEmptyObjectsAndEmptyDictionaries_ButKeepArrays()
    {
        var json = JsonSerializer.Serialize(
            new { outer = new { inner = new { value = (string?)null } }, meta = new Dictionary<string, string>(), tags = Array.Empty<string>(), kept = new { value = 0 } },
            McpResultJson.Options);

        json.ShouldBe("""{"tags":[],"kept":{"value":0}}""");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Options_CyclicGraph_FailsWithTheSerializerError_NotAStackOverflow()
    {
        var node = new Node();
        node.Next = node;

        Should.Throw<JsonException>(() => JsonSerializer.Serialize(node, McpResultJson.Options));
    }
}
