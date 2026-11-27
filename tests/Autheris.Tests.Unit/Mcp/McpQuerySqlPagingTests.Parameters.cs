namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed partial class McpQuerySqlPagingTests
{
    private sealed class CapturingSqlService(IGovernedSqlExecutionService inner) : IGovernedSqlExecutionService
    {
        public List<GovernedSqlQueryRequest> Requests { get; } = [];

        public bool ThrowPagingUnsupportedOnce { get; set; }

        public ClaimsPrincipal? User { get; private set; }

        public TenantId Tenant { get; private set; }

        public Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            Func<System.Data.Common.DbDataReader, GovernedResultInfo, System.Threading.CancellationToken, Task> rowWriter,
            System.Threading.CancellationToken ct = default)
        {
            Record(request, user, tenantId);
            return inner.ExecuteGovernedQueryAsync(request, user, tenantId, rowWriter, ct);
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            System.Threading.CancellationToken ct = default)
        {
            Record(request, user, tenantId);
            if (ThrowPagingUnsupportedOnce && request.Page is not null)
            {
                ThrowPagingUnsupportedOnce = false;
                throw new TrinoSqlEngine.Ast.Emit.SqlPagingUnsupportedException();
            }

            return inner.ExecuteQueryBufferedAsync(request, user, tenantId, ct);
        }

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, System.Threading.CancellationToken ct = default) =>
            inner.ValidateAsync(rawSql, user, tenantId, ct);

        public Task<GovernedSqlResult> RunAsync(GovernedSqlQueryRequest request) =>
            inner.ExecuteQueryBufferedAsync(request, User!, Tenant);

        private void Record(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId)
        {
            Requests.Add(request);
            User = user;
            Tenant = tenantId;
        }
    }

    private const string ParamSql = "SELECT id, name FROM sales.public.orders WHERE name <> @n ORDER BY id";

    private static string Args(string query, object? parameters = null, string? cursor = null, int? limit = null)
    {
        var map = new Dictionary<string, object?> { ["query"] = query };
        if (parameters is not null)
        {
            map["parameters"] = parameters;
        }

        if (cursor is not null)
        {
            map["cursor"] = cursor;
        }

        if (limit is not null)
        {
            map["limit"] = limit;
        }

        return JsonSerializer.Serialize(map);
    }

    private static string? ErrorCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private static async Task<string> MappedCodeAsync(McpToolExecutionHandler handler, string argsJson)
    {
        var ex = await Should.ThrowAsync<Exception>(() => handler.ExecuteToolAsync(QuerySqlTool, argsJson, CreateSession()));
        SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        return mapping.Code;
    }

    private static IEnumerable<object?> Flatten(List<IReadOnlyList<object?>> bound) => bound.SelectMany(b => b);

    private static async Task<(string? Json, Exception? Error)> TryRunAsync(McpToolExecutionHandler handler, string argsJson)
    {
        try
        {
            return (await handler.ExecuteToolAsync(QuerySqlTool, argsJson, CreateSession()), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ScalarParameter_IsBoundNeverInterpolated()
    {
        var bound = new List<IReadOnlyList<object?>>();
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5, boundValues: bound);
        const string value = "x' OR '1'='1";

        var json = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = value }), CreateSession());

        ErrorCode(json).ShouldBeNull();
        executedSql.ShouldNotBeEmpty();
        executedSql.ShouldAllBe(sql => !sql.Contains(value, StringComparison.Ordinal) && !sql.Contains("'1'='1", StringComparison.Ordinal));
        Flatten(bound).ShouldContain(value);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ListParameter_InList_IsBoundAsSeparateValues()
    {
        var bound = new List<IReadOnlyList<object?>>();
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5, boundValues: bound);

        var json = await handler.ExecuteToolAsync(
            QuerySqlTool,
            Args("SELECT id, name FROM sales.public.orders WHERE name IN (@names) ORDER BY id", new { names = new[] { "row-1", "row-2", "a,b" } }),
            CreateSession());

        ErrorCode(json).ShouldBeNull();
        var flat = Flatten(bound).ToList();
        flat.ShouldContain("row-1");
        flat.ShouldContain("row-2");
        flat.ShouldContain("a,b");
        flat.Any(v => v is JsonElement).ShouldBeFalse();
        executedSql.ShouldAllBe(sql => !sql.Contains("row-1", StringComparison.Ordinal) && !sql.Contains("a,b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuerySql_MissingParameter_IsRefusedBeforeAnythingRuns()
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5);

        // an unsupplied placeholder is not rewritten into a bind slot, so the statement does not parse (as on POST /api/v1/sql)
        var mcp = await TryRunAsync(handler, Args(ParamSql, new { other = "x" }));

        mcp.Error.ShouldBeOfType<TrinoSqlEngine.SqlSyntaxErrorException>();
        executedSql.ShouldBeEmpty();
    }

    private const string OptionalSql = "SELECT id, name FROM sales.public.orders WHERE (@n IS NULL OR name <> @n) ORDER BY id";

    [Fact]
    public async Task QuerySql_JsonNullParameter_DropsTheOptionalFilter()
    {
        var bound = new List<IReadOnlyList<object?>>();
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5, boundValues: bound);

        var json = await handler.ExecuteToolAsync(QuerySqlTool, """{"query":"SELECT id, name FROM sales.public.orders WHERE (@n IS NULL OR name <> @n) ORDER BY id","parameters":{"n":null}}""", CreateSession());

        ErrorCode(json).ShouldBeNull();
        executedSql.ShouldNotBeEmpty();
        executedSql.ShouldAllBe(sql => !sql.Contains("<>", StringComparison.Ordinal) && !sql.Contains("IS NULL", StringComparison.OrdinalIgnoreCase));
        Flatten(bound).ShouldAllBe(v => v != null);
    }

    [Fact]
    public async Task QuerySql_SuppliedValue_KeepsTheOptionalFilter()
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5);

        var json = await handler.ExecuteToolAsync(QuerySqlTool, Args(OptionalSql, new { n = "x" }), CreateSession());

        ErrorCode(json).ShouldBeNull();
        executedSql.ShouldNotBeEmpty();
        executedSql.ShouldAllBe(sql => sql.Contains("<>", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_AbsentParameter_InOptionalFilter_IsStillRefused()
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5);

        // an absent parameter is never read as NULL: it must not drop the predicate
        var mcp = await TryRunAsync(handler, Args(OptionalSql, new { other = "x" }));

        mcp.Error.ShouldBeOfType<TrinoSqlEngine.SqlSyntaxErrorException>();
        executedSql.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ListOutsideIn_MapsToListParamPosition()
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5);

        (await MappedCodeAsync(handler, Args("SELECT id FROM sales.public.orders WHERE name = @n", new { n = new[] { "a", "b" } })))
            .ShouldBe("SQL_LIST_PARAM_POSITION");
        executedSql.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_InvalidParametersArgument_ReturnsInvalidParams()
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5);
        var bodies = new[]
        {
            """{"query":"SELECT id FROM sales.public.orders WHERE name = @a","parameters":"secret-7f3a"}""",
            """{"query":"SELECT id FROM sales.public.orders WHERE name = @a","parameters":["secret-7f3a"]}""",
            """{"query":"SELECT id FROM sales.public.orders WHERE name = @a","parameters":{"a":"secret-7f3a","A":"secret-7f3a"}}""",
            """{"query":"SELECT id FROM sales.public.orders WHERE name = @a","parameters":{"a":"secret-7f3a","@a":"secret-7f3a"}}""",
            """{"query":"SELECT id FROM sales.public.orders WHERE name = @a","parameters":{"a":"secret-7f3a","a":"secret-7f3a"}}""",
            """{"query":"SELECT id FROM sales.public.orders WHERE name = @a","parameters":{"a":{"nested":"secret-7f3a"}}}"""
        };

        foreach (var body in bodies)
        {
            var json = await handler.ExecuteToolAsync(QuerySqlTool, body, CreateSession());
            ErrorCode(json).ShouldBe("INVALID_PARAMS", body);
            json.ShouldNotContain("secret-7f3a");
            json.ShouldNotContain("nested");
        }

        executedSql.ShouldBeEmpty();
    }

    [Fact]
    public async Task QuerySql_LiteralRejected_RetriedWithParameter_Succeeds()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 5);

        (await MappedCodeAsync(handler, Args("SELECT id, name FROM sales.public.orders WHERE name = 'a\\b' ORDER BY id")))
            .ShouldBe("SQL_LITERAL_NOT_PERMITTED");

        var json = await handler.ExecuteToolAsync(
            QuerySqlTool, Args("SELECT id, name FROM sales.public.orders WHERE name = @n ORDER BY id", new { n = "a\\b" }), CreateSession());
        ErrorCode(json).ShouldBeNull();
        JsonDocument.Parse(json).RootElement.GetProperty("rowCount").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task QuerySql_CursorWithSameParameters_ReturnsNextPage()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession();

        var first = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }), session);
        using var firstDoc = JsonDocument.Parse(first);
        firstDoc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([1, 2]);
        var cursor = firstDoc.RootElement.GetProperty("cursor").GetString()!;

        var second = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }, cursor), session);
        using var secondDoc = JsonDocument.Parse(second);
        secondDoc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ShouldBe([3, 4]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_CursorWithChangedAddedOrDroppedParameters_FailsClosed()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 2);
        var session = CreateSession();
        var first = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }), session);
        var cursor = JsonDocument.Parse(first).RootElement.GetProperty("cursor").GetString()!;

        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        var tamperedCode = ErrorCode(await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }, tampered), session));
        tamperedCode.ShouldBe("INVALID_PARAMS");

        var variants = new object[]
        {
            new { n = "yyy" },
            new { n = "zzz", extra = "x" },
            new { N = "zzz" }
        };
        foreach (var variant in variants)
        {
            var json = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, variant, cursor), session);
            ErrorCode(json).ShouldBe(tamperedCode, JsonSerializer.Serialize(variant));
        }

        // dropped parameters: the placeholder is no longer bound, the call fails before any row is read
        var dropped = await TryRunAsync(handler, Args(ParamSql, null, cursor));
        (dropped.Error is not null || ErrorCode(dropped.Json!) is not null).ShouldBeTrue();
    }

    [Fact]
    public void QuerySqlToolDefinition_ExposesOptionalParametersObject()
    {
        using var doc = JsonDocument.Parse(QuerySqlTool.InputJsonSchema);
        var props = doc.RootElement.GetProperty("properties");

        props.TryGetProperty("parameters", out var parameters).ShouldBeTrue();
        parameters.GetProperty("type").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "object", "null" });
        doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["query"]);
        QuerySqlTool.Description!.ShouldContain("@name");
        QuerySqlTool.Description!.ShouldNotContain("inline", Case.Insensitive);
        parameters.GetProperty("description").GetString()!.ShouldContain("never inlined");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ParameterInIdentifierPosition_FailsLikeWebSql()
    {
        var statements = new[]
        {
            "SELECT id FROM @t",
            "SELECT @c FROM sales.public.orders",
            "SELECT @t.id FROM sales.public.orders"
        };

        foreach (var statement in statements)
        {
            var capture = new List<CapturingSqlService>();
            var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5, capture: capture);

            var mcp = await TryRunAsync(handler, Args(statement, new { t = "evil_tbl_7f3a", c = "evil_col_7f3a" }));

            Exception? web = null;
            try
            {
                capture.Count.ShouldBe(1);
                await capture[0].RunAsync(
                    new GovernedSqlQueryRequest(
                        statement,
                        Parameters: new Dictionary<string, object?>
                        {
                            ["t"] = JsonDocument.Parse("\"evil_tbl_7f3a\"").RootElement.Clone(),
                            ["c"] = JsonDocument.Parse("\"evil_col_7f3a\"").RootElement.Clone()
                        },
                        Transport: SqlTransports.WebSql));
            }
            catch (Exception ex)
            {
                web = ex;
            }

            // a value never names a table or column: both doors agree, and no executed text carries the value
            (mcp.Error is null).ShouldBe(web is null, statement);
            executedSql.ShouldAllBe(sql => !sql.Contains("evil_", StringComparison.Ordinal), statement);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_InvalidParameterErrors_DoNotEchoValuesOrNames()
    {
        const string secret = "secret-7f3a";
        var (handler, _, _) = CreatePipeline(maxResultRows: 5);
        const string q = "SELECT id FROM sales.public.orders WHERE name = @a";
        const string qIn = "SELECT id FROM sales.public.orders WHERE name IN (@a)";
        var bodies = new[]
        {
            Args(q, secret),
            Args(q, new { a = new { nested = secret } }),
            "{\"query\":\"" + q + "\",\"parameters\":{\"a\":\"" + secret + "\",\"A\":\"" + secret + "\"}}",
            Args(q, new { unsentName = secret }),
            Args(q, new { a = new[] { secret, secret } }),
            Args(qIn, new { a = new object?[] { secret, null } }),
            Args(qIn, new { a = new object?[] { secret, 1 } }),
            Args("SELECT id FROM sales.public.orders WHERE name = 'a\\b'", new { unsentName = secret })
        };

        foreach (var body in bodies)
        {
            var outcome = await TryRunAsync(handler, body);
            if (outcome.Error is not null)
            {
                var serialized = SqlCompilerErrorMapper.TryMap(outcome.Error, out var mapping)
                    ? JsonSerializer.Serialize(mapping)
                    : outcome.Error.Message;
                serialized.ShouldNotContain(secret);
                serialized.ShouldNotContain("unsentName");
                serialized.ShouldNotContain("nested");
            }
            else
            {
                ErrorCode(outcome.Json!).ShouldNotBeNull(body);
                outcome.Json!.ShouldNotContain(secret);
                outcome.Json!.ShouldNotContain("unsentName");
                outcome.Json!.ShouldNotContain("nested");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_CursorFromOtherTransport_FailsClosed()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 2, capture: capture);
        var session = CreateSession();
        var webParameters = new Dictionary<string, object?> { ["n"] = JsonDocument.Parse("\"zzz\"").RootElement.Clone() };

        var mcpFirst = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }), session);
        var cursor = JsonDocument.Parse(mcpFirst).RootElement.GetProperty("cursor").GetString()!;

        var webResult = await capture[0].RunAsync(
            new GovernedSqlQueryRequest(ParamSql, Parameters: webParameters, Transport: SqlTransports.WebSql, Page: new SqlPageWindow(0, 2, 1)));
        webResult.Next.ShouldNotBeNullOrWhiteSpace();

        ErrorCode(await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }, webResult.Next), session)).ShouldBe("INVALID_PARAMS");
        ErrorCode(await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { N = "zzz" }, cursor), session)).ShouldBe("INVALID_PARAMS");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_BothRequestConstructions_CarrySameParameters()
    {
        var capture = new List<CapturingSqlService>();
        var (handler, _, _) = CreatePipeline(maxResultRows: 5, capture: capture);
        capture[0].ThrowPagingUnsupportedOnce = true;
        const string marker = "marker-1192";

        var json = await handler.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = marker }), CreateSession());
        ErrorCode(json).ShouldBeNull();

        // first the paged request (refused by the fake), then the unpaged fallback request
        var requests = capture[0].Requests;
        requests.Count.ShouldBe(2);
        requests[0].Page.ShouldNotBeNull();
        requests[1].Page.ShouldBeNull();
        requests[0].Parameters.ShouldNotBeNull();
        requests[1].Parameters.ShouldBeSameAs(requests[0].Parameters);
        ((JsonElement)requests[1].Parameters!["n"]!).GetString().ShouldBe(marker);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_PagedRewrite_StatementTextIdenticalWithAndWithoutParameters()
    {
        var (withParams, _, sqlWith) = CreatePipeline(maxResultRows: 2);
        var (withoutParams, _, sqlWithout) = CreatePipeline(maxResultRows: 2);

        await withParams.ExecuteToolAsync(QuerySqlTool, Args(ParamSql, new { n = "zzz" }), CreateSession());
        await withoutParams.ExecuteToolAsync(
            QuerySqlTool, Args("SELECT id, name FROM sales.public.orders WHERE name <> 'zzz' ORDER BY id"), CreateSession());

        sqlWith.Count.ShouldBe(1);
        sqlWithout.Count.ShouldBe(1);
        sqlWith[0].ShouldBe(sqlWithout[0]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_ParameterizedQuery_StillAppliesColumnMasking()
    {
        var (handler, _, _) = CreatePipeline(maxResultRows: 5, maskEmail: true);
        using var scope = McpResultMasking.Begin();

        var json = await handler.ExecuteToolAsync(
            QuerySqlTool,
            Args("SELECT id, name, email FROM sales.public.orders WHERE name <> @n ORDER BY id", new { n = "zzz" }),
            CreateSession());

        ErrorCode(json).ShouldBeNull();
        json.ShouldNotContain("@secret.example");
        scope.Outcome.ShouldBe(true);
    }
}
