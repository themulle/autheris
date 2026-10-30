namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Identity.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mesh.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Final fixes of the no-legacy-error-bodies PR: NL-SC-36..39 and NL-T-36..40.</summary>
[Trait("Category", "Security")]
public sealed class FinalErrorBodyFixesTests
{
    private static DefaultHttpContext NewContext()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<string> BodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return await new StreamReader(ctx.Response.Body).ReadToEndAsync();
    }

    [Fact]
    public async Task NL_T_36_Scim_Error_Bodies_Are_Fixed_And_Do_Not_Echo_Input()
    {
        // NL-SC-36: both details are compile-time constants; the filter text, principal and tenant never appear.
        var filterCtx = NewContext();
        var rejection = ScimFilterGuard.RejectUnsupportedFilter("userName eq \"secret-user@tenant-42\"");
        rejection.ShouldNotBeNull();
        await rejection.ExecuteAsync(filterCtx);
        var filterBody = await BodyAsync(filterCtx);
        filterCtx.Response.StatusCode.ShouldBe(400);
        using (var doc = JsonDocument.Parse(filterBody))
        {
            doc.RootElement.GetProperty("detail").GetString().ShouldBe(ScimFilterGuard.UnsupportedFilterDetail);
            doc.RootElement.GetProperty("scimType").GetString().ShouldBe("invalidFilter");
        }

        filterBody.ShouldNotContain("secret-user");
        filterBody.ShouldNotContain("tenant-42");

        var throttleCtx = NewContext();
        await ScimMassChangeResult.TooManyRequests(new ScimMassChangeBlockedException(91)).ExecuteAsync(throttleCtx);
        var throttleBody = await BodyAsync(throttleCtx);
        throttleCtx.Response.StatusCode.ShouldBe(429);
        using (var doc = JsonDocument.Parse(throttleBody))
        {
            doc.RootElement.GetProperty("detail").GetString().ShouldBe(ScimMassChangeResult.ThrottledDetail);
        }
    }

    [Fact]
    public async Task NL_T_37_WebSql_Timeout_Problem_Code_Is_Governed_And_Detail_Fixed()
    {
        // NL-SC-37: a governed code is kept; anything else falls back to the status default, the detail stays fixed.
        ApiErrorCodes.IsGoverned("SQL_TIMEOUT").ShouldBeTrue();
        var write = typeof(WebSqlEndpoints).GetMethod("WriteTimeoutProblemAsync", BindingFlags.NonPublic | BindingFlags.Static);
        write.ShouldNotBeNull();

        foreach (var (input, expected) in new[]
                 {
                     ("SQL_TIMEOUT", "SQL_TIMEOUT"),
                     ("select secret from hr.salaries", ApiErrorCodes.Resolve(504, null)),
                     ("lower_case_code", ApiErrorCodes.Resolve(504, null)),
                     (new string('A', 200), ApiErrorCodes.Resolve(504, null))
                 })
        {
            var ctx = NewContext();
            await (Task)write.Invoke(null, [ctx, input, CancellationToken.None])!;
            var raw = await BodyAsync(ctx);
            using var doc = JsonDocument.Parse(raw);
            doc.RootElement.GetProperty("code").GetString().ShouldBe(expected);
            doc.RootElement.GetProperty("detail").GetString().ShouldBe(WebSqlEndpoints.GenericTimeoutMessage);
            raw.ShouldNotContain("salaries");
        }
    }

    [Fact]
    public async Task NL_T_38_Envoy_Deny_Body_Is_Sanitized_And_Never_Carries_Decision_Reason()
    {
        // NL-SC-38: the denied reasons (policy internals) stay in the log; the body is the fixed message.
        var policy = Substitute.For<IPolicyEnforcementService>();
        var service = new EnvoyExtAuthzService(policy, NullLogger<EnvoyExtAuthzService>.Instance);
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessDecision(
                Table: new TableIdentifier("default", "public", "customers"),
                IsAllowed: false,
                ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
                CombinedRowFilterSql: null,
                DeniedReasons: ["internal: rule p42 matched for tenant-secret"],
                HasUnconstrainedColumnAllow: false));

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", "alice"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            ],
            "MeshTls"));
        var response = await service.CheckHttpAsync("GET", "/api/v1/customers", new Dictionary<string, string>(), caller);

        var body = response.HttpResponse.DeniedResponse?.Body;
        if (body is not null)
        {
            body.ShouldNotContain("p42");
            body.ShouldNotContain("tenant-secret");
        }

        // The endpoint text passes the control-character strip and the length cap.
        var sanitized = ApiProblems.SanitizeOperatorText("a\r\nb\u0000" + new string('x', 5000), "fallback");
        sanitized.ShouldNotContain("\r");
        sanitized.ShouldNotContain("\n");
        sanitized.ShouldNotContain("\u0000");
        sanitized.Length.ShouldBeLessThan(2000);
        ApiProblems.SanitizeOperatorText(null, "fallback").ShouldBe("fallback");
    }

    [Theory]
    [InlineData("""{"error":{"code":"lower case <script>","message":"m"}}""")]
    [InlineData("""{"isError":true,"error":{"code":"x\nforged audit line","message":"m"}}""")]
    [InlineData("""{"status":500,"code":"not governed because lower"}""")]
    [InlineData("""{"error":{"code":"ERR_TOO_LONG_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","message":"m"}}""")]
    public void NL_T_39_Guardrail_Caps_And_Governs_The_Audit_Code(string json)
    {
        // NL-SC-39: a code outside the governed format never reaches the audit trail; the fixed marker is used.
        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue();
        code.ShouldBe("ERROR");
    }

    [Fact]
    public void NL_T_39_Guardrail_Keeps_A_Governed_Code()
    {
        AiDataGuardrailService.TryGetExecutorError("""{"error":{"code":"ACCESS_DENIED","message":"m"}}""", out var code).ShouldBeTrue();
        code.ShouldBe("ACCESS_DENIED");
    }

    [Theory]
    [InlineData("""{"error":{"code":"X1"}}""")]
    [InlineData("""{"error":{"name":"NotFoundError"}}""")]
    [InlineData("""{"error":{}}""")]
    public void NL_T_40_Guardrail_Denies_Object_Error_Without_String_Message(string json)
    {
        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue(json);
        code.ShouldBe("ERROR");
    }

    [Theory]
    [InlineData("""{"error":"x"}""")]
    [InlineData("""{"error":"x","status":200}""")]
    public void NL_T_40_Bare_String_Error_Without_Error_Status_Is_Unproducible_And_Treated_As_Data(string json)
    {
        // No producer remains: NoNewBareErrorBodiesTests (baseline empty) and NoRawJsonErrorWritersTests (Results.* forms) forbid it,
        // so a top-level string "error" member is ordinary data and is not denied.
        AiDataGuardrailService.TryGetExecutorError(json, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ApiErrorCodes.InvalidTableIdentifier)]
    [InlineData(ApiErrorCodes.TableNotRegistered)]
    [InlineData(ApiErrorCodes.TotpCodeRequired)]
    [InlineData(ApiErrorCodes.TotpNoPendingEnrollment)]
    [InlineData(ApiErrorCodes.TotpCurrentCodeRequired)]
    [InlineData(ApiErrorCodes.TotpCodeInvalid)]
    public void Final_Fix_Codes_Are_Governed(string code) => ApiErrorCodes.IsGoverned(code).ShouldBeTrue();
}
