namespace Autheris.Tests.Unit.Api;

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

/// <summary>O-5b step 2 WP-0: NL-SC-27 (governed codes, extension allowlist), NL-SC-28 (indistinguishable pairs), NL-SC-30 (operator text).</summary>
[Trait("Category", "Security")]
public sealed class ApiProblemsHardeningTests
{
    [Theory]
    [InlineData("NOT_A_REGISTERED_CODE")]
    [InlineData("lower_case")]
    [InlineData("BAD<script>alert(1)</script>")]
    [InlineData("X")]
    [InlineData("")]
    [InlineData("SECRET_TABLE_finance.payroll")]
    public void ApiProblems_Rejects_Ungoverned_Dynamic_Code_And_Uses_Status_Default(string code)
    {
        // NL-T-27 / NL-SC-27
        var problem = ApiProblems.CreateProblemDetails(StatusCodes.Status403Forbidden, code, "Denied.");
        problem.Extensions["code"].ShouldBe(ApiErrorCodes.AccessDenied);
        problem.Type.ShouldBe("urn:autheris:error:ACCESS_DENIED");

        var result = ApiProblems.Problem(StatusCodes.Status409Conflict, code, "Conflict.").ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        result.Extensions["code"].ShouldBe(ApiErrorCodes.Conflict);
        result.Type!.ShouldNotContain(code.Length > 0 ? code : "\u0001");
    }

    [Theory]
    [InlineData(400, "BAD_REQUEST")]
    [InlineData(401, "UNAUTHORIZED")]
    [InlineData(403, "ACCESS_DENIED")]
    [InlineData(404, "RESOURCE_NOT_FOUND")]
    [InlineData(409, "CONFLICT")]
    [InlineData(413, "PAYLOAD_TOO_LARGE")]
    [InlineData(422, "UNPROCESSABLE_ENTITY")]
    [InlineData(429, "TOO_MANY_REQUESTS")]
    [InlineData(500, "INTERNAL_ERROR")]
    [InlineData(501, "NOT_IMPLEMENTED")]
    [InlineData(503, "SERVICE_UNAVAILABLE")]
    [InlineData(418, "BAD_REQUEST")]
    [InlineData(599, "INTERNAL_ERROR")]
    public void Status_Default_Code_Table(int status, string expected) =>
        ApiErrorCodes.StatusDefault(status).ShouldBe(expected);

    [Fact]
    public void Governed_Registered_Codes_Pass_Through()
    {
        ApiErrorCodes.IsGoverned(ApiErrorCodes.ProcedureArgumentInvalid).ShouldBeTrue();
        ApiErrorCodes.IsGoverned("SQL_SYNTAX_ERROR").ShouldBeTrue();
        ApiErrorCodes.IsGoverned("CDC_WATERMARK_EXPIRED").ShouldBeTrue();
        ApiErrorCodes.IsGoverned("INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM").ShouldBeTrue();
        ApiErrorCodes.IsGoverned(ApiErrorCodes.ProcedureRejected).ShouldBeTrue();
        ApiProblems.CreateProblemDetails(400, "SQL_SYNTAX_ERROR", "x").Extensions["code"].ShouldBe("SQL_SYNTAX_ERROR");
    }

    [Fact]
    public void Problem_Extensions_Outside_Allowlist_Are_Dropped()
    {
        // NL-T-29 / NL-SC-27
        var ext = new Dictionary<string, object?>
        {
            ["retryAfterSeconds"] = 5,
            ["maxValidTo"] = "2027-01-01T00:00:00Z",
            ["limit"] = 100,
            ["phase"] = "sunset",
            ["sunsetDate"] = "2027-01-01",
            ["replacement"] = "/v2/x",
            ["discardedEvents"] = 3,
            ["error"] = "legacy",
            ["traceId"] = "forged",
            ["code"] = "FORGED",
            ["message"] = "leak",
            ["nested"] = new { a = 1 },
            ["list"] = new[] { 1, 2 }
        };

        var problem = ApiProblems.Problem(429, ApiErrorCodes.TooManyRequests, "Slow down.", extraExtensions: ext).ShouldBeOfType<ProblemHttpResult>().ProblemDetails;

        problem.Extensions["retryAfterSeconds"].ShouldBe(5);
        problem.Extensions["limit"].ShouldBe(100);
        problem.Extensions["discardedEvents"].ShouldBe(3);
        foreach (var dropped in new[] { "error", "message", "nested", "list" })
        {
            problem.Extensions.ContainsKey(dropped).ShouldBeFalse(dropped);
        }

        problem.Extensions["code"].ShouldBe(ApiErrorCodes.TooManyRequests);
        problem.Extensions["traceId"].ShouldNotBe("forged");
    }

    [Fact]
    public async Task WriteAsync_Writes_Problem_Json_With_Governed_Code_And_Allowlisted_Extensions()
    {
        var ctx = NewContext();
        await ApiProblems.WriteAsync(ctx, 400, "BOGUS_CODE_XYZ", "Fixed.", CancellationToken.None, new Dictionary<string, object?> { ["limit"] = 7, ["error"] = "x" });

        ctx.Response.StatusCode.ShouldBe(400);
        ctx.Response.ContentType.ShouldStartWith("application/problem+json");
        using var doc = JsonDocument.Parse(Body(ctx));
        doc.RootElement.GetProperty("code").GetString().ShouldBe("BAD_REQUEST");
        doc.RootElement.GetProperty("detail").GetString().ShouldBe("Fixed.");
        doc.RootElement.GetProperty("limit").GetInt32().ShouldBe(7);
        doc.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteTooManyRequests_Sets_RetryAfter_Header_And_Member_Consistently()
    {
        var ctx = NewContext();
        await ApiProblems.WriteTooManyRequests(ctx, ApiErrorCodes.TooManyRequests, "Slow down.", 7, CancellationToken.None);

        ctx.Response.StatusCode.ShouldBe(429);
        ctx.Response.Headers.RetryAfter.ToString().ShouldBe("7");
        using var doc = JsonDocument.Parse(Body(ctx));
        doc.RootElement.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(7);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TOO_MANY_REQUESTS");
    }

    [Fact]
    public async Task WriteAsync_Rejects_Null_Detail() =>
        await Should.ThrowAsync<ArgumentNullException>(() => ApiProblems.WriteAsync(NewContext(), 400, "BAD_REQUEST", null!, CancellationToken.None));

    [Fact]
    public void Legacy_Forbidden_And_NotFound_Codes_Map_To_Governed_Codes()
    {
        // NL-T-35 / NL-SC-20: an unmapped legacy code never leaks as-is.
        ApiProblems.CreateProblemDetails(403, "FORBIDDEN", "x").Extensions["code"].ShouldBe(ApiErrorCodes.AccessDenied);
        ApiProblems.CreateProblemDetails(404, "NOT_FOUND", "x").Extensions["code"].ShouldBe(ApiErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task Indistinguishable_Pair_Bodies_And_Headers_Are_Identical_Across_Principals()
    {
        // NL-T-28 / NL-SC-28
        var a = NewContext("trace-a", "corr-a", "alice", "/api/x/one");
        var b = NewContext("trace-b", "corr-b", "bob", "/api/x/two");
        var c = NewContext("trace-c", null, null, "/api/x/three");

        await ApiProblems.Indistinguishable(404, ApiErrorCodes.ResourceNotFound, "The requested resource was not found.").ExecuteAsync(a);
        await ApiProblems.Indistinguishable(404, ApiErrorCodes.ResourceNotFound, "The requested resource was not found.").ExecuteAsync(b);
        await ApiProblems.Indistinguishable(404, ApiErrorCodes.ResourceNotFound, "The requested resource was not found.").ExecuteAsync(c);

        var bodyA = Body(a);
        Body(b).ShouldBe(bodyA);
        Body(c).ShouldBe(bodyA);
        a.Response.StatusCode.ShouldBe(404);
        b.Response.StatusCode.ShouldBe(404);
        a.Response.ContentType.ShouldBe(b.Response.ContentType);
        a.Response.Headers.Select(h => h.Key + "=" + h.Value).Order().ShouldBe(b.Response.Headers.Select(h => h.Key + "=" + h.Value).Order());
        a.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();

        using var doc = JsonDocument.Parse(bodyA);
        foreach (var forbidden in new[] { "traceId", "correlationId", "instance" })
        {
            doc.RootElement.TryGetProperty(forbidden, out _).ShouldBeFalse(forbidden);
        }

        bodyA.ShouldNotContain("trace-");
        bodyA.ShouldNotContain("corr-");
        bodyA.ShouldNotContain("/api/x");
        doc.RootElement.GetProperty("code").GetString().ShouldBe("RESOURCE_NOT_FOUND");
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("urn:autheris:error:RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Indistinguishable_Falls_Back_To_Status_Default_For_Ungoverned_Code()
    {
        var ctx = NewContext();
        await ApiProblems.Indistinguishable(403, "NOPE_NOT_REGISTERED", "Denied.").ExecuteAsync(ctx);
        using var doc = JsonDocument.Parse(Body(ctx));
        doc.RootElement.GetProperty("code").GetString().ShouldBe("ACCESS_DENIED");
    }

    [Theory]
    [InlineData("Plain text.", "Plain text.")]
    [InlineData("Line1\r\nLine2\tTab\u0000Nul\u001b[31mred", "Line1Line2TabNul[31mred")]
    [InlineData("  padded  ", "padded")]
    public void Operator_Mapped_Message_Is_Sanitized(string input, string expected)
    {
        // NL-T-31 / NL-SC-30
        ApiProblems.SanitizeOperatorText(input, "fallback").ShouldBe(expected);
    }

    [Fact]
    public void Operator_Mapped_Message_Is_Length_Capped_And_Empty_Uses_Fallback()
    {
        var capped = ApiProblems.SanitizeOperatorText(new string('a', 5000), "fallback");
        capped.Length.ShouldBe(ApiProblems.MaxOperatorTextLength);
        ApiProblems.SanitizeOperatorText("\u0001\u0002", "fallback").ShouldBe("fallback");
        ApiProblems.SanitizeOperatorText(null, "fallback").ShouldBe("fallback");
        ApiProblems.SanitizeOperatorText("  ", "fallback").ShouldBe("fallback");
    }

    private static DefaultHttpContext NewContext(string trace = "t", string? correlation = null, string? user = null, string path = "/x")
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = trace };
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = path;
        if (correlation != null)
        {
            ctx.Request.Headers["X-Correlation-Id"] = correlation;
        }

        if (user != null)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"));
        }

        return ctx;
    }

    private static string Body(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
    }
}
