namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.State.Caching.Services;
using Autheris.Tests.Unit.RateLimiting;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>query_sql takes and returns the WebSQL contract: sql / dataSource / page.size / next in, the POST /api/v1/sql body out.</summary>
public sealed partial class McpQuerySqlPagingTests
{
    private const string Ordered = "SELECT id, name FROM sales.public.orders ORDER BY id";

    private static string Call(string sql, object? page = null, string? next = null, string? dataSource = null, object? nextRaw = null, object? pageRaw = null, object? dataSourceRaw = null)
    {
        var map = new Dictionary<string, object?> { ["sql"] = sql };
        if (page is not null || pageRaw is not null)
        {
            map["page"] = pageRaw ?? page;
        }

        if (next is not null || nextRaw is not null)
        {
            map["next"] = nextRaw ?? next;
        }

        if (dataSource is not null || dataSourceRaw is not null)
        {
            map["dataSource"] = dataSourceRaw ?? dataSource;
        }

        return JsonSerializer.Serialize(map);
    }

    private static string ErrorCodeOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static int[] IdsOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToArray();
    }

    private static string? NextOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("next", out var next) ? next.GetString() : null;
    }

    private static async Task<string> RunAsync(McpToolExecutionHandler handler, string args, string? user = null, string tenant = "tenant-1") =>
        await handler.ExecuteToolAsync(QuerySqlTool, args, user is null ? CreateSession(tenantId: tenant) : CreateSession(user, tenant));

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_PageSizeAboveCap_ReducedBeforeExecution_ReportsPageSizeCapped()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, webSqlMaxAllowedRows: 10, capture: capture);
        using var scope = McpRowContinuation.Begin();

        var first = await RunAsync(handler, Call(Ordered, new { size = 4 }));

        capture[0].Requests[0].Page!.Size.ShouldBe(2, "the cap applies before execution");
        IdsOf(first).ShouldBe([1, 2]);
        using (var doc = JsonDocument.Parse(first))
        {
            doc.RootElement.GetProperty("page").GetProperty("size").GetInt32().ShouldBe(2);
            doc.RootElement.TryGetProperty("rowCap", out _).ShouldBeFalse();
        }

        scope.PageSizeCapped.ShouldBeTrue();
        IdsOf(await RunAsync(handler, Call(Ordered, next: NextOf(first)))).ShouldBe([3, 4]);
    }

    [Fact]
    public async Task QuerySql_PageSizeWithinCap_NotCapped()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 3, webSqlMaxAllowedRows: 10, capture: capture);
        using var scope = McpRowContinuation.Begin();

        var json = await RunAsync(handler, Call(Ordered, new { size = 2 }));

        capture[0].Requests[0].Page!.Size.ShouldBe(2);
        IdsOf(json).ShouldBe([1, 2]);
        scope.PageSizeCapped.ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("""{"size":0}""")]
    [InlineData("""{"size":-1}""")]
    [InlineData("""{"size":4}""")]
    [InlineData("""{"size":1.5}""")]
    [InlineData("""{"size":"2"}""")]
    [InlineData("""{"size":99999999999}""")]
    [InlineData("""{"size":null}""")]
    [InlineData("""{}""")]
    public async Task QuerySql_PageSizeOutsideWebSqlRange_IsInvalidParams(string page)
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, webSqlMaxAllowedRows: 3, capture: capture);

        var json = await RunAsync(handler, $$"""{"sql":"{{Ordered}}","page":{{page}}}""");

        ErrorCodeOf(json).ShouldBe("INVALID_PARAMS");
        capture[0].Requests.ShouldBeEmpty("no executor call for an invalid page size");
    }

    [Fact]
    public async Task QuerySql_WithoutPage_PagesAtCap_NextIsValid()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, capture: capture);
        using var scope = McpRowContinuation.Begin();

        var first = await RunAsync(handler, Call(Ordered));

        capture[0].Requests[0].Page!.Size.ShouldBe(2);
        scope.PageSizeCapped.ShouldBeFalse("nothing was asked, so nothing was reduced");
        NextOf(first).ShouldNotBeNullOrWhiteSpace();
        IdsOf(await RunAsync(handler, Call(Ordered, next: NextOf(first)))).ShouldBe([3, 4]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ScopeMaxResultRows_ClampsPageSize_ReportsPageSizeCapped()
    {
        var options = ScopeQueryExecutionGuardTests.CreateGatewayOptions(
            scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Agent.Read"] = new() { MaxResultRows = 3 }
            },
            maxResultRowsGlobal: 500);
        var guard = new ScopeQueryExecutionGuard(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options)));
        var rows = Enumerable.Range(1, 3).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i, ["name"] = "n" + i }).ToList();
        var stub = new StubGovernedSqlExecutionService
        {
            OnBuffered = (_, _, _, _) => Task.FromResult(new GovernedSqlResult("x", ["id", "name"], rows, 3, 1, Page: new GovernedSqlPageResponse(1, 0, 3), Next: "tok"))
        };
        var handler = new McpToolExecutionHandler(
            stub, new StubDataQueryService(), new StubCatalogDiscoveryService(), new StubApiDispatcherService(),
            new InMemoryLineageGraphStore(), new InMemoryTableMetadataRepository(),
            gatewayOptions: Options.Create(options), logger: NullLogger<McpToolExecutionHandler>.Instance, scopeGuard: guard);
        var session = CreateSession() with { AdditionalClaims = new Dictionary<string, string> { ["scp"] = "Agent.Read" } };
        using var scope = McpRowContinuation.Begin();

        await handler.ExecuteToolAsync(QuerySqlTool, Call(Ordered, new { size = 10 }), session);

        stub.Requests[0].Page!.Size.ShouldBe(3);
        scope.PageSizeCapped.ShouldBeTrue();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    public async Task QuerySql_InvalidOrOverlongNext_IsSqlPagingTokenInvalid(string next)
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);

        ErrorCodeOf(await RunAsync(handler, Call(Ordered, next: next))).ShouldBe("SQL_PAGING_TOKEN_INVALID");
        ErrorCodeOf(await RunAsync(handler, Call(Ordered, next: new string('x', 513)))).ShouldBe("SQL_PAGING_TOKEN_INVALID");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("""{"sql":"SELECT 1","next":123}""")]
    [InlineData("""{"sql":"SELECT 1","next":true}""")]
    [InlineData("""{"sql":"SELECT 1","page":5}""")]
    [InlineData("""{"sql":"SELECT 1","page":"x"}""")]
    [InlineData("""{"sql":"SELECT 1","page":[1]}""")]
    [InlineData("""{"sql":"SELECT 1","dataSource":5}""")]
    [InlineData("""{"sql":5}""")]
    public async Task QuerySql_NonStringNextOrNonObjectPage_IsInvalidParams(string args)
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);

        ErrorCodeOf(await RunAsync(handler, args)).ShouldBe("INVALID_PARAMS");
    }

    [Fact]
    public async Task QuerySql_DataSource_PassedToExecutorAndTokenBinding()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, withDemoSource: true, capture: capture);
        const string demoSql = "SELECT id, name FROM demo.public.orders ORDER BY id";

        var first = await RunAsync(handler, Call(demoSql, dataSource: "demo"));
        var second = await RunAsync(handler, Call(demoSql, next: NextOf(first), dataSource: "demo"));

        capture[0].Requests.ShouldAllBe(r => r.DataSourceName == "demo");
        IdsOf(second).ShouldBe([3, 4]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_BothExecutorRequestsCarryDataSourceParametersTransport()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, capture: capture);
        capture.Count.ShouldBe(1);
        capture[0].ThrowPagingUnsupportedOnce = true;

        var json = await RunAsync(handler, """{"sql":"SELECT id, name FROM sales.public.orders WHERE name <> @n ORDER BY id","dataSource":"sales","parameters":{"n":"zzz"}}""");

        json.ShouldNotContain("\"error\"");
        var requests = capture[0].Requests;
        requests.Count.ShouldBe(2, "the paged attempt and the unpaged fallback");
        requests.ShouldAllBe(r => r.DataSourceName == "sales" && r.Transport == SqlTransports.Mcp && r.Parameters != null && r.Parameters.ContainsKey("n"));
        requests[0].Page.ShouldNotBeNull();
        requests[1].Page.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_Result_HasNoRowCapAndNoMcpOnlyFields()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);

        using var doc = JsonDocument.Parse(await RunAsync(handler, Call(Ordered)));

        var allowed = new HashSet<string> { "columns", "rows", "rowCount", "truncated", "page", "next", "pagingUnavailable", "indexHints" };
        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldAllBe(n => allowed.Contains(n));
        doc.RootElement.GetProperty("columns")[0].GetProperty("name").GetString().ShouldBe("id");
        doc.RootElement.GetProperty("columns")[0].TryGetProperty("type", out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ExecutorWebSqlPolicyException_FixedMessageNoExceptionText()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, capture: capture);
        capture[0].FailWith = _ => new WebSqlPolicyException("source 'secret-source' is not enabled");

        // The handler lets the exception through; the executor maps it with the fixed message of the code (never Exception.Message).
        await Should.ThrowAsync<WebSqlPolicyException>(() => RunAsync(handler, Call(Ordered, dataSource: "sales")));

        var envelope = McpToolError.FailSql(new WebSqlPolicyException("source 'secret-source' is not enabled")).ContentJson;
        ErrorCodeOf(envelope).ShouldBe("SQL_POLICY_VIOLATION");
        envelope.ShouldNotContain("secret-source");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_NextWithoutPage_StillCarriesExecutedPageSize()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, webSqlMaxAllowedRows: 10, capture: capture);
        var first = await RunAsync(handler, Call(Ordered, new { size = 2 }));

        await RunAsync(handler, Call(Ordered, new { size = 2 }, next: NextOf(first)));

        var continuation = capture[0].Requests[^1];
        continuation.NextToken.ShouldNotBeNull();
        continuation.Page.ShouldNotBeNull("an explicit page.size is always sent with the token, so a mismatch fails");
        continuation.Page!.Size.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_NextWithoutPageSize_ContinuesWithTokenSize()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 3, webSqlMaxAllowedRows: 10, capture: capture);
        var first = await RunAsync(handler, Call(Ordered, new { size = 1 }));

        var second = await RunAsync(handler, Call(Ordered, next: NextOf(first)));

        capture[0].Requests[^1].Page.ShouldBeNull("like POST /api/v1/sql, size and offset come from the verified token");
        IdsOf(second).ShouldBe([2]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_NextWithoutPageSize_TokenAboveCurrentCap_IsSqlPagingTokenInvalid_NoRows()
    {
        var (large, _, _) = CreatePipeline(maxResultRows: 4);
        var (small, _, _) = CreatePipeline(maxResultRows: 2);
        var token = NextOf(await RunAsync(large, Call(Ordered)));

        var json = await RunAsync(small, Call(Ordered, next: token));

        ErrorCodeOf(json).ShouldBe("SQL_PAGING_TOKEN_INVALID");
        json.ShouldNotContain("\"rows\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_NextMintedUnderLargerCap_IsSqlPagingTokenInvalid()
    {
        var (large, _, _) = CreatePipeline(maxResultRows: 4);
        var (small, _, _) = CreatePipeline(maxResultRows: 2);
        var first = await RunAsync(large, Call(Ordered));
        var token = NextOf(first);
        token.ShouldNotBeNull();

        ErrorCodeOf(await RunAsync(small, Call(Ordered, next: token))).ShouldBe("SQL_PAGING_TOKEN_INVALID");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ExecutorReturnsMoreRowsThanSize_CutTruncatedNoNext()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, capture: capture);
        capture[0].RewriteResult = r => r with
        {
            Rows = [.. r.Rows, .. r.Rows, .. r.Rows],
            RowCount = r.Rows.Count * 3,
            Next = "leaked-token"
        };

        var json = await RunAsync(handler, Call(Ordered));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("rows").GetArrayLength().ShouldBe(2);
        doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        doc.RootElement.TryGetProperty("next", out _).ShouldBeFalse();
        json.ShouldNotContain("leaked-token");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("""{"size":0}""")]
    [InlineData("""{"size":2147483648}""")]
    [InlineData("""{"size":[2]}""")]
    [InlineData("""{"size":true}""")]
    public async Task QuerySql_PageSizeInvalidShapes_InvalidParams(string page)
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);

        ErrorCodeOf(await RunAsync(handler, $$"""{"sql":"{{Ordered}}","page":{{page}}}""")).ShouldBe("INVALID_PARAMS");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_PageSizeCapped_OnlyOnSuccess_NoCapValue()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, webSqlMaxAllowedRows: 10);

        using (var failed = McpRowContinuation.Begin())
        {
            var error = await RunAsync(handler, Call(Ordered, new { size = 5 }, next: "not-a-token"));

            ErrorCodeOf(error).ShouldBe("SQL_PAGING_TOKEN_INVALID");
            failed.PageSizeCapped.ShouldBeFalse();
        }

        using var ok = McpRowContinuation.Begin();
        var success = await RunAsync(handler, Call(Ordered, new { size = 5 }));
        ok.PageSizeCapped.ShouldBeTrue();
        using var doc = JsonDocument.Parse(success);
        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldNotContain("pageSizeCapped");
        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldNotContain("rowCap");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_AllTokenFailures_IdenticalBytes_NoExceptionText()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var token = NextOf(await RunAsync(handler, Call(Ordered)))!;
        var failures = new[]
        {
            await RunAsync(handler, Call(Ordered, next: token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1])),
            await RunAsync(handler, Call(Ordered, next: token), user: "S-1-5-21-1055-OTHER"),
            await RunAsync(handler, Call(Ordered, next: token), tenant: "tenant-2"),
            await RunAsync(handler, Call(Ordered + " DESC", next: token)),
            await RunAsync(handler, Call(Ordered, next: "")),
            await RunAsync(handler, Call(Ordered, next: new string('x', 513))),
        };

        failures.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        ErrorCodeOf(failures[0]).ShouldBe("SQL_PAGING_TOKEN_INVALID");
        failures[0].ShouldNotContain("Exception");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_NoUnpagedFallbackWhenNextOrPage()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, webSqlMaxAllowedRows: 10, capture: capture);

        capture[0].ThrowPagingUnsupportedOnce = true;
        ErrorCodeOf(await RunAsync(handler, Call(Ordered, new { size = 2 }))).ShouldBe("SQL_PAGING_UNSUPPORTED");
        capture[0].Requests.Count.ShouldBe(1);

        capture[0].ThrowPagingUnsupportedOnce = true;
        var first = capture[0].Requests.Count;
        ErrorCodeOf(await RunAsync(handler, Call(Ordered, next: "any"))).ShouldBeOneOf("SQL_PAGING_UNSUPPORTED", "SQL_PAGING_TOKEN_INVALID");
        capture[0].Requests.Count.ShouldBe(first + 1, "one attempt, no unpaged retry");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ShortenedNextRoundTripsWithoutPage()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var session = CreateSession();
        var (resume, first) = await FirstPageAsync(handler, session);
        first.GetProperty("rows").GetArrayLength().ShouldBe(4);
        var token = resume!(2)!;

        var json = await RunAsync(handler, Call(OrderedSql, next: token));

        IdsOf(json).ShouldBe([3, 4, 5]);
    }
}
