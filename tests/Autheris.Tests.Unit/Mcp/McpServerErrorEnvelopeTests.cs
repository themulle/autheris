namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Mcp;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

/// <summary>The one exit of every tool failure: an error result carrying the envelope as text and as structured content.</summary>
[Trait("Category", "Security")]
public sealed class McpServerErrorEnvelopeTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";

    private sealed class FakeGuardrail(Func<McpToolCallRequest, CancellationToken, McpToolCallResult> respond) : IAiDataGuardrailService
    {
        public ValueTask<McpToolCallResult> ExecuteToolWithGuardrailAsync(McpToolCallRequest request, McpSessionContext sessionContext, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(respond(request, cancellationToken));
    }

    private static ServiceProvider Services(IAiDataGuardrailService guardrail, InMemoryAuditLogRepository? audit = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "Bearer"))
        };
        http.Items[Autheris.Domain.Security.SecurityPrincipalContext.TenantIdItemKey] = new TenantId(Tenant);
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http });
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()));
        services.AddSingleton(guardrail);
        if (audit is not null)
        {
            services.AddSingleton<Autheris.Application.Common.Interfaces.IAuditLogRepository>(audit);
            services.AddSingleton<Autheris.Application.Audit.IAuditWriter>(new Autheris.Application.Audit.AuditWriter(audit, Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Application.Audit.AuditWriter>.Instance));
        }

        return services.BuildServiceProvider();
    }

    private static async Task<CallToolResult> Call(IAiDataGuardrailService guardrail, string tool = "describe_dataset", InMemoryAuditLogRepository? audit = null, CancellationToken ct = default)
    {
        await using var sp = Services(guardrail, audit);
        return await GatewayMcpServer.CallGuardedToolAsync(sp, tool, "{}", ct);
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    private static FakeGuardrail Returning(McpToolCallResult result) => new((_, _) => result);

    private static FakeGuardrail Throwing(Exception ex) => new((_, _) => throw ex);

    [Fact]
    public async Task Failure_IsError_TextEqualsStructuredContent()
    {
        var result = await Call(Returning(McpToolError.Fail(McpErrorCodes.DatasetNotFound)));

        result.IsError.ShouldBe(true);
        result.Meta.ShouldBeNull();
        McpErrorEnvelopeAssert.Code(Text(result)).ShouldBe("DATASET_NOT_FOUND");
        result.StructuredContent.ShouldNotBeNull();
        result.StructuredContent!.Value.GetRawText().ShouldBe(Text(result));
    }

    [Fact]
    public async Task CallGuardedTool_StructuredContentEqualsText_OnlyErrorMember()
    {
        var results = new[]
        {
            await Call(Returning(McpToolError.Fail(McpErrorCodes.InvalidParams))),
            await Call(Returning(new McpToolCallResult(false, "{}", "Tool 'x' denied for tenant t with password=p"))),
            await Call(Throwing(new InvalidOperationException("boom")))
        };

        foreach (var result in results)
        {
            result.IsError.ShouldBe(true);
            JsonSerializer.Serialize(result.StructuredContent).ShouldBe(JsonSerializer.Serialize(JsonDocument.Parse(Text(result)).RootElement));
            McpErrorEnvelopeAssert.Error(Text(result));
            using var doc = JsonDocument.Parse(Text(result));
            doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["error"]);
        }
    }

    [Fact]
    public async Task LegacyFreeText_NormalizedToExecutionFailed()
    {
        var result = await Call(Returning(new McpToolCallResult(false, "{}", "Access denied to tool 'q' for tenant acme")));

        McpErrorEnvelopeAssert.Code(Text(result)).ShouldBe("EXECUTION_FAILED");
        Text(result).ShouldNotContain("acme");
    }

    [Fact]
    public async Task CallGuardedTool_UnmappedException_ReturnsInternalError_NoExceptionText()
    {
        var result = await Call(Throwing(new InvalidOperationException("password=hunter2 on table sales.public.secret")), "query_sql");

        result.IsError.ShouldBe(true);
        McpErrorEnvelopeAssert.Code(Text(result)).ShouldBe("INTERNAL_ERROR");
        McpErrorEnvelopeAssert.Message(Text(result)).ShouldBe("An internal error occurred.");
        Text(result).ShouldNotContain("hunter2");
        Text(result).ShouldNotContain("query_sql");
        Text(result).ShouldNotContain("An error occurred invoking");
    }

    [Fact]
    public async Task CallGuardedTool_CallerCancellation_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Call(Throwing(new OperationCanceledException(cts.Token)), ct: cts.Token));
    }

    [Fact]
    public async Task CallGuardedTool_TimeoutOce_ReturnsQueryTimeout()
    {
        var result = await Call(Throwing(new OperationCanceledException("http client timeout")));

        result.IsError.ShouldBe(true);
        McpErrorEnvelopeAssert.Code(Text(result)).ShouldBe("QUERY_TIMEOUT");
        Text(result).ShouldNotContain("http client");
    }

    [Theory]
    [InlineData("describe_dataset")]
    [InlineData("query_sql")]
    [InlineData("query_metrics")]
    [InlineData("list_orders")]
    public async Task CallGuardedTool_ThrownSecurityException_EqualsReturnedDenyBytes(string tool)
    {
        var returned = await Call(Returning(McpToolError.Fail(McpErrorCodes.TargetDeniedCode(tool))), tool);

        foreach (var ex in new Exception[]
                 {
                     new Autheris.Domain.Exceptions.GatewaySecurityException("policy detail", "FORBIDDEN"),
                     new UnauthorizedAccessException("no"),
                     new System.Security.SecurityException("no")
                 })
        {
            var thrown = await Call(Throwing(ex), tool);
            thrown.IsError.ShouldBe(true);
            Text(thrown).ShouldBe(Text(returned));
        }
    }

    [Fact]
    public async Task CallGuardedTool_CatchAll_WritesDenyAudit_NeverSuccess()
    {
        var audit = new InMemoryAuditLogRepository();

        var result = await Call(Throwing(new InvalidOperationException("password=hunter2")), "query_sql", audit);

        result.IsError.ShouldBe(true);
        var entry = audit.Entries.Single();
        entry.Decision.ShouldBe("DENY");
        entry.EventType.ShouldBe("MCP_TOOL_EXECUTION");
        entry.DetailsJson.ShouldNotContain("hunter2");
        entry.DetailsJson.ShouldContain(McpToolFailureRecorder.FixedDetails);

        audit.FailWith = new InvalidOperationException("audit store down");
        var withBrokenAudit = await Call(Throwing(new InvalidOperationException("x")), "query_sql", audit);
        withBrokenAudit.IsError.ShouldBe(true);
        McpErrorEnvelopeAssert.Code(Text(withBrokenAudit)).ShouldBe("INTERNAL_ERROR");
    }

    [Fact]
    public async Task Success_NoStructuredContent_KeepsMeta()
    {
        var result = await Call(Returning(new McpToolCallResult(true, """{"rows":[]}""", EstimatedTokens: 7, IsMasked: true, TruncatedDueToBudget: false)));

        result.IsError.ShouldBe(false);
        result.StructuredContent.ShouldBeNull();
        Text(result).ShouldBe("""{"rows":[]}""");
        result.Meta!["isMasked"]!.GetValue<bool>().ShouldBeTrue();
        result.Meta["estimatedTokens"]!.GetValue<int>().ShouldBe(7);
        result.Meta["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Success_WithErrorMemberInContent_IsNotAnError()
    {
        // The guardrail classifies once; the server trusts IsSuccess and never sniffs the payload.
        var result = await Call(Returning(new McpToolCallResult(true, """{"error":{"code":"X","message":"a column named error"}}""")));

        result.IsError.ShouldBe(false);
    }

    [Fact]
    public async Task CallGuardedTool_RowShortened_MetaHasTruncationReasonAndReturnedRows()
    {
        var result = await Call(Returning(new McpToolCallResult(true, """{"rows":[{"id":1}]}""", EstimatedTokens: 9, TruncatedDueToBudget: true, ReturnedRows: 1)));

        result.IsError.ShouldBe(false);
        result.Meta!["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result.Meta["truncationReason"]!.GetValue<string>().ShouldBe("TOKEN_BUDGET");
        result.Meta["returnedRows"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task CallGuardedTool_UnderBudget_MetaHasNoTruncationReason()
    {
        var result = await Call(Returning(new McpToolCallResult(true, """{"rows":[]}""", EstimatedTokens: 3)));

        result.Meta!.ContainsKey("truncationReason").ShouldBeFalse();
        result.Meta.ContainsKey("returnedRows").ShouldBeFalse();
    }
}
