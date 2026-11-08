namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-SIMPL-53 PR 1 (#578): Mcp:MaxResultRows is enforced server-side on all five row tools.</summary>
public sealed class McpRowCapTests
{
    private const int Cap = 20;
    private const int Produced = 50;

    public sealed class Tag { public string Name { get; set; } = "t"; }

    public sealed class Order
    {
        public int Id { get; set; }
        public List<Tag> Tags { get; set; } = Enumerable.Range(0, Produced).Select(_ => new Tag()).ToList();
    }

    public sealed class Query
    {
        public List<Order> Orders => Enumerable.Range(0, Produced).Select(i => new Order { Id = i }).ToList();
    }

    public static TheoryData<string> Tools => new() { "query_dataset", "query_sql", "call_procedure", "sample_rows", "query_graphql" };

    private static string RowsJson(string arrayName, int n, string countName) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [arrayName] = Enumerable.Range(0, n).Select(i => new { id = i }).ToArray(),
            [countName] = n,
            ["next"] = "cursor",
        });

    private static IOptionsMonitor<GatewayOptions> Options(int maxRows)
    {
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(new GatewayOptions { Mcp = new McpOptions { MaxResultRows = maxRows } });
        return monitor;
    }

    private sealed record Fixture(
        GatewayMcpQueryExecutor Executor, IMcpToolExecutionHandler Handler, IMcpDatasetCatalog Catalog);

    private static async Task<Fixture> CreateAsync(int maxRows = Cap, int producedRows = Produced)
    {
        var handler = Substitute.For<IMcpToolExecutionHandler>();
        handler.CanHandle(Arg.Any<string>()).Returns(ci => ci.Arg<string>() is "query_sql" or "query_dataset" or "call_procedure");
        handler.ExecuteToolAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<McpToolDefinition>().Name switch
            {
                "query_dataset" => RowsJson("data", producedRows, "count"),
                _ => RowsJson("rows", producedRows, "rowCount"),
            });

        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.SampleRowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => new McpDatasetSample("a.b.c", producedRows,
                Enumerable.Range(0, producedRows).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i }).ToList()));

        var requestExecutor = await new ServiceCollection().AddGraphQL().AddQueryType<Query>().BuildRequestExecutorAsync();
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(requestExecutor);

        var executor = new GatewayMcpQueryExecutor(provider, Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance, datasetCatalog: catalog, toolExecutionHandler: handler,
            gatewayOptions: Options(maxRows));
        return new Fixture(executor, handler, catalog);
    }

    private static McpToolDefinition Tool(string name) =>
        new McpToolRegistry(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), new FixedDemoData()).FindTool(name)
        ?? throw new InvalidOperationException(name);

    private sealed class FixedDemoData : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    private static string ArgsFor(string tool, string? limitJson = null) => tool switch
    {
        "query_dataset" => $$"""{"dataset":"a.b.c"{{(limitJson is null ? "" : $",\"limit\":{limitJson}")}}}""",
        "query_sql" => """{"query":"SELECT * FROM a.b.c LIMIT 1000"}""",
        "call_procedure" => $$"""{"procedure_name":"p"{{(limitJson is null ? "" : $",\"max_rows\":{limitJson}")}}}""",
        "sample_rows" => $$"""{"dataset":"a.b.c"{{(limitJson is null ? "" : $",\"count\":{limitJson}")}}}""",
        _ => """{"query":"{ orders { id } }"}""",
    };

    private static async Task<JsonElement> RunAsync(Fixture f, string tool, string args)
    {
        var json = await f.Executor.ExecuteOperationAsync(Tool(tool), args, Session());
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static int RowsOf(string tool, JsonElement root) => tool switch
    {
        "query_dataset" => root.GetProperty("data").GetArrayLength(),
        "query_graphql" => root.GetProperty("data").GetProperty("orders").GetArrayLength(),
        _ => root.GetProperty("rows").GetArrayLength(),
    };

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Each_row_tool_returns_at_most_option_rows(string tool)
    {
        var f = await CreateAsync();

        var root = await RunAsync(f, tool, ArgsFor(tool));

        RowsOf(tool, root).ShouldBe(Math.Min(Cap, tool == "sample_rows" ? McpDatasetCatalog.MaxSampleRows : Cap));
    }

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(Tools))]
    public async Task Larger_client_limit_does_not_raise_cap(string tool)
    {
        // SEC-53-01
        var f = await CreateAsync();

        var root = await RunAsync(f, tool, ArgsFor(tool, "1000"));

        RowsOf(tool, root).ShouldBeLessThanOrEqualTo(Cap);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("query_dataset", "0")]
    [InlineData("query_dataset", "-1")]
    [InlineData("query_dataset", "\"abc\"")]
    [InlineData("query_dataset", "99999999999")]
    [InlineData("query_dataset", "1e30")]
    [InlineData("query_dataset", "2.5")]
    [InlineData("call_procedure", "0")]
    [InlineData("call_procedure", "-1")]
    [InlineData("call_procedure", "\"abc\"")]
    [InlineData("call_procedure", "99999999999")]
    [InlineData("sample_rows", "0")]
    [InlineData("sample_rows", "-5")]
    [InlineData("sample_rows", "\"x\"")]
    [InlineData("sample_rows", "99999999999")]
    [InlineData("sample_rows", "true")]
    public async Task Invalid_client_limit_is_rejected_or_clamped(string tool, string limit)
    {
        // SEC-53-02 (owner Q2: rejected)
        var f = await CreateAsync();

        var root = await RunAsync(f, tool, ArgsFor(tool, limit));

        root.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
        await f.Handler.DidNotReceiveWithAnyArgs().ExecuteToolAsync(default!, default!, default!, default);
        await f.Catalog.DidNotReceiveWithAnyArgs().SampleRowsAsync(default!, default!, default, default, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_client_LIMIT_above_cap_is_trimmed()
    {
        // SEC-53-01: the statement text is not trusted; the executor trims.
        var f = await CreateAsync();

        var root = await RunAsync(f, "query_sql", """{"query":"SELECT * FROM a.b.c LIMIT 100000"}""");

        root.GetProperty("rows").GetArrayLength().ShouldBe(Cap);
        root.GetProperty("rowCount").GetInt32().ShouldBe(Cap);
        root.TryGetProperty("next", out _).ShouldBeFalse("a continuation cursor would skip the trimmed rows");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQl_nested_list_and_alias_trimmed_to_cap()
    {
        // SEC-53-04
        var f = await CreateAsync();

        var root = await RunAsync(f, "query_graphql",
            """{"query":"{ a: orders { id tags { name } } b: orders { id } }"}""");

        var data = root.GetProperty("data");
        data.GetProperty("a").GetArrayLength().ShouldBe(Cap);
        data.GetProperty("b").GetArrayLength().ShouldBe(Cap);
        data.GetProperty("a")[0].GetProperty("tags").GetArrayLength().ShouldBe(Cap);
        root.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GraphQl_cap_applies_per_root_list()
    {
        var f = await CreateAsync();

        var root = await RunAsync(f, "query_graphql", """{"query":"{ x: orders { id } y: orders { id } }"}""");

        root.GetProperty("data").GetProperty("x").GetArrayLength().ShouldBe(Cap);
        root.GetProperty("data").GetProperty("y").GetArrayLength().ShouldBe(Cap);
    }

    [Fact]
    public async Task Smaller_client_limit_is_kept()
    {
        var f = await CreateAsync();

        await RunAsync(f, "sample_rows", ArgsFor("sample_rows", "2"));

        await f.Catalog.Received(1).SampleRowsAsync(Arg.Any<ClaimsPrincipal>(), "a.b.c", 2, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Truncated_flag_carries_no_hidden_row_count()
    {
        // SEC-53-05, SEC-53-08: numeric cap only, no total, no remaining count, no row values in the note.
        var f = await CreateAsync();

        var root = await RunAsync(f, "query_sql", ArgsFor("query_sql"));

        root.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        root.GetProperty("rowCap").GetInt32().ShouldBe(Cap);
        root.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["rowCap", "rowCount", "rows", "truncated"]);
        root.GetRawText().ShouldNotContain(Produced.ToString());
    }

    [Fact]
    public async Task Truncated_flag_set_only_when_rows_cut()
    {
        var f = await CreateAsync(producedRows: Cap);

        var root = await RunAsync(f, "query_sql", ArgsFor("query_sql"));

        root.TryGetProperty("truncated", out _).ShouldBeFalse();
        root.TryGetProperty("rowCap", out _).ShouldBeFalse();
        root.GetProperty("rows").GetArrayLength().ShouldBe(Cap);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cap_applies_after_row_policy()
    {
        // SEC-53-06: the executor only sees the policy-filtered rows; fewer than the cap come back untouched and unflagged.
        var f = await CreateAsync(producedRows: 7);

        var root = await RunAsync(f, "query_sql", ArgsFor("query_sql"));

        root.GetProperty("rows").GetArrayLength().ShouldBe(7);
        root.TryGetProperty("truncated", out _).ShouldBeFalse();
        await f.Handler.Received(1).ExecuteToolAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(20, 20)]
    [InlineData(50000, 10000)]
    public void Misconfigured_option_falls_back_to_default_not_unbounded(int option, int expected)
    {
        // SEC-53-03
        McpRowCap.ResolveCap(option).ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Misconfigured_option_executor_uses_default_cap()
    {
        var f = await CreateAsync(maxRows: 0, producedRows: 500);

        var root = await RunAsync(f, "query_sql", ArgsFor("query_sql"));

        root.GetProperty("rows").GetArrayLength().ShouldBe(100);
    }

    [Theory]
    [InlineData(null, 20, 20)]
    [InlineData(5, 20, 5)]
    [InlineData(500, 20, 20)]
    public void Effective_is_the_minimum_of_client_and_option(int? requested, int cap, int expected) =>
        McpRowCap.Effective(requested, cap).ShouldBe(expected);
}
