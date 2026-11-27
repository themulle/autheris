namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Tests.Common;
using Autheris.Application.Sql.Paging;
using TrinoSqlEngine.Ast.Emit;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed partial class McpQuerySqlPagingTests
{
    private const string OrderedSql = "SELECT id, name FROM sales.public.orders ORDER BY id";

    private static async Task<(Func<int, string?>? ResumeAfter, JsonElement Root)> FirstPageAsync(
        McpToolExecutionHandler handler, McpSessionContext session, string sql = OrderedSql)
    {
        using var scope = McpRowContinuation.Begin();
        var json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql }), session);
        return (scope.ResumeAfter, JsonDocument.Parse(json).RootElement.Clone());
    }

    private static async Task<int[]> PageIdsAsync(McpToolExecutionHandler handler, McpSessionContext session, string cursor, string sql = OrderedSql)
    {
        var json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = sql, cursor }), session);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToArray();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeAfterK_NextCallStartsAtRowKPlusOne_NoGapNoDuplicate()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var session = CreateSession();
        var (resume, first) = await FirstPageAsync(handler, session);
        var firstIds = first.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToArray();
        firstIds.ShouldBe([1, 2, 3, 4]);

        for (var k = 1; k < 4; k++)
        {
            var token = resume!(k);
            token.ShouldNotBeNullOrWhiteSpace();
            var rest = await PageIdsAsync(handler, session, token!);
            rest.ShouldBe(Enumerable.Range(k + 1, 5 - k).ToArray());
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeToken_DecodesAtOffsetPlusK_AndNextOfResumedPageIsValid()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession();
        var (resume, _) = await FirstPageAsync(handler, session);
        var token = resume!(1)!;

        // The resumed page starts at row 2 (offset 1) and its own next continues at row 4 (offset 3, number 2).
        var json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = OrderedSql, cursor = token }), session);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToArray().ShouldBe([2, 3]);
        var next = doc.RootElement.GetProperty("next").GetString()!;
        (await PageIdsAsync(handler, session, next)).ShouldBe([4, 5]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeToken_Tampered_IsInvalidParams()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var session = CreateSession();
        var (resume, _) = await FirstPageAsync(handler, session);
        var token = resume!(2)!;
        var tampered = FlipMidMacChar(token);

        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = OrderedSql, cursor = tampered }), session));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeToken_OtherParametersOrStatement_IsInvalidParams()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var session = CreateSession();
        var (resume, _) = await FirstPageAsync(handler, session);
        var token = resume!(2)!;

        const string changed = "SELECT id, name FROM sales.public.orders ORDER BY id DESC";
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = changed, cursor = token }), session));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeToken_OtherUserOrTenant_IsInvalidParams()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var owner = CreateSession("S-1-5-21-1055-OWNER", "tenant-1");
        var (resume, _) = await FirstPageAsync(handler, owner);
        var token = resume!(2)!;

        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = OrderedSql, cursor = token }), CreateSession("S-1-5-21-1055-OTHER", "tenant-1")));
        AssertInvalidParams(await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = OrderedSql, cursor = token }), CreateSession("S-1-5-21-1055-OWNER", "tenant-2")));
    }

    /// <summary>Changes a character in the middle of the signature part, which always changes the decoded bytes.</summary>
    private static string FlipMidMacChar(string token)
    {
        var index = token.IndexOf('.', StringComparison.Ordinal) + 8;
        return token[..index] + (token[index] == 'A' ? 'B' : 'A') + token[(index + 1)..];
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeToken_ReplayOnOtherTransport_IsInvalidParams()
    {
        WebSqlPageTokenCodec? codec = null;
        var (handler, _, _) = CreatePipeline(maxResultRows: 4, captureCodec: c => codec = c);
        var session = CreateSession();
        var (resume, _) = await FirstPageAsync(handler, session);
        var token = resume!(2)!;
        var user = McpProtocolHandler.BuildPrincipalFromSession(session, TimeProvider.System)!;
        var tenant = new TenantId("tenant-1");

        // The minted token decodes for the MCP transport it was issued for and for no other one.
        codec!.DecodeToken(token, OrderedSql, null, user, tenant, "sales", SqlTransports.Mcp, 10_000).Offset.ShouldBe(2);
        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(token, OrderedSql, null, user, tenant, "sales", "WebSql", 10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeAfter_OutOfRangeOrBeyondMaxOffset_ReturnsNull()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var (resume, _) = await FirstPageAsync(handler, CreateSession());

        resume!(0).ShouldBeNull();
        resume(-1).ShouldBeNull();
        resume(4).ShouldBeNull();
        resume(int.MaxValue).ShouldBeNull();
        resume(int.MinValue).ShouldBeNull();
        resume(3).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeAfter_AtMaxOffsetBoundary_ReturnsNullBeyondAndTokenAtBoundary()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4, maxOffset: 2);
        var session = CreateSession();
        var (resume, _) = await FirstPageAsync(handler, session);

        resume!(2).ShouldNotBeNull();
        resume(3).ShouldBeNull();
        (await PageIdsAsync(handler, session, resume(2)!)).ShouldBe([3, 4, 5]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeAfter_NoSid_ReturnsNull()
    {
        IGovernedSqlExecutionService? service = null;
        CreatePipeline(maxResultRows: 4, captureService: s => service = s);
        var withSid = McpProtocolHandler.BuildPrincipalFromSession(CreateSession(), TimeProvider.System)!;
        ((ClaimsIdentity)withSid.Identity!).AddClaim(new Claim("tid", "tenant-1"));

        async Task<Func<int, string?>?> ResumeForAsync(ClaimsPrincipal user)
        {
            var result = await service!.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(OrderedSql, Transport: SqlTransports.Mcp, Page: new SqlPageWindow(0, 4, 1), OnPageOutcome: _ => { }),
                user,
                new TenantId("tenant-1"));
            return result.ResumeAfter;
        }

        var resumeWithSid = (await ResumeForAsync(withSid))!;
        resumeWithSid(2).ShouldNotBeNull();

        // The minter re-checks the caller at call time: once the principal has no SID it mints nothing.
        var identity = (ClaimsIdentity)withSid.Identity!;
        foreach (var claim in identity.Claims.Where(c => c.Type is ClaimTypes.PrimarySid or ClaimTypes.NameIdentifier or "objectSid" or "onprem_sid" or "primarysid" or "oid" or "sub" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid" or "http://schemas.microsoft.com/identity/claims/objectidentifier").ToList())
        {
            identity.RemoveClaim(claim);
        }

        resumeWithSid(2).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ResumeAfter_ShortLastPage_IsBoundedByDeliveredRows()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 4);
        var session = CreateSession();
        var (firstResume, _) = await FirstPageAsync(handler, session);
        var lastPageCursor = firstResume!(3)!;

        // The page at offset 3 delivers two rows (4 and 5) although its window is four wide.
        using var scope = McpRowContinuation.Begin();
        var json = await handler.ExecuteToolAsync(QuerySqlTool, JsonSerializer.Serialize(new { query = OrderedSql, cursor = lastPageCursor }), session);
        JsonDocument.Parse(json).RootElement.GetProperty("rows").GetArrayLength().ShouldBe(2);

        scope.ResumeAfter!(1).ShouldNotBeNull();
        scope.ResumeAfter(2).ShouldBeNull();
        scope.ResumeAfter(3).ShouldBeNull();
    }

    [Fact]
    public async Task QuerySql_NonPageableQuery_OffersNoContinuation()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);

        var (resume, root) = await FirstPageAsync(handler, CreateSession(), "SELECT id, name FROM sales.public.orders");

        root.GetProperty("pagingUnavailable").GetString().ShouldBe("order_by_required");
        resume.ShouldBeNull();
    }
}
