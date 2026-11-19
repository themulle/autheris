namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.Mcp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class AiDataGuardrailSecurityContextTests
{
    [Fact]
    public async Task AiDataGuardrailService_CustomClientIp_PassedToSecurityContext()
    {
        // Finding B2 / F-03 Verification:
        // ClientIp in McpSessionContext must be passed to SecurityEvaluationContext rather than hardcoded 127.0.0.1.
        var registry = new McpToolRegistry();
        Autheris.Application.Policy.Pdp.AccessRequest? capturedContext = null;

        var policyService = Autheris.Tests.Unit.TestSupport.PdpTestDoubles.From(ctx =>
        {
            capturedContext = ctx;
            return TableAccessDecision.Allowed(ctx.Object.Identifier, new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        });

        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, decisionPoint: policyService, tableMetadataRepository: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AnyTable());

        var session = new McpSessionContext(
            SessionId: "sess-ip-test",
            ServicePrincipalId: "agent-remote",
            TenantId: "tenant-ip",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            ClientIp: "198.51.100.42");

        var request = new McpToolCallRequest("query_customers", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        capturedContext.ShouldNotBeNull();
        capturedContext.Subject.ClientIp!.ToString().ShouldBe("198.51.100.42");
    }

    [Fact]
    public async Task AiDataGuardrailService_ExplicitToolTargetTable_CheckedByPolicy()
    {
        // Finding B2 Verification:
        // McpToolDefinition with explicit TargetTable must pass that TableIdentifier to the policy engine.
        var registry = new McpToolRegistry();
        var explicitTable = new TableIdentifier("sales", "crm", "deals");
        var dealTool = new McpToolDefinition(
            Name: "query_deals",
            Description: "Deals query tool",
            InputJsonSchema: "{}",
            TargetGraphQLOperation: "query Deals { deals { id amount } }",
            TargetTable: explicitTable);
        registry.RegisterTool(dealTool);

        TableIdentifier? evaluatedTable = null;
        var policyService = Autheris.Tests.Unit.TestSupport.PdpTestDoubles.From(ctx =>
        {
            evaluatedTable = ctx.Object.Identifier;
            return TableAccessDecision.Allowed(ctx.Object.Identifier, new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        });

        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, decisionPoint: policyService, tableMetadataRepository: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AnyTable());

        var session = new McpSessionContext("sess-deals", "agent-deals", "tenant-sales", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_deals", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        evaluatedTable.ShouldBe(explicitTable);
    }
}
