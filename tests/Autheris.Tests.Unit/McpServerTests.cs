namespace Autheris.Tests.Unit;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpServerTests
{
    [Fact]
    public void McpToolRegistry_ShouldContainDefaultTools()
    {
        // Arrange
        var registry = new McpToolRegistry();

        // Act
        var tools = registry.GetAvailableTools();
        var customerTool = registry.FindTool("query_customers");
        var invoiceTool = registry.FindTool("query_invoices");
        var catalogTool = registry.FindTool("query_data_catalog");

        // Assert
        tools.Count.ShouldBeGreaterThanOrEqualTo(3);
        customerTool.ShouldNotBeNull();
        customerTool.Description.ShouldContain("customer records");
        invoiceTool.ShouldNotBeNull();
        catalogTool.ShouldNotBeNull();
    }

    [Fact]
    public void McpToolRegistry_CustomToolRegistration_ShouldBeRetrievable()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var custom = new McpToolDefinition(
            Name: "custom_analytics",
            Description: "Analyzes quarterly metrics",
            InputJsonSchema: """{"type":"object"}""",
            TargetGraphQLOperation: "query GetAnalytics { metrics { revenue } }"
        );

        // Act
        registry.RegisterTool(custom);
        var found = registry.FindTool("custom_analytics");

        // Assert
        found.ShouldNotBeNull();
        found.Name.ShouldBe("custom_analytics");
        found.TargetGraphQLOperation.ShouldContain("GetAnalytics");
    }

    [Fact]
    public async Task AiDataGuardrailService_ShouldMaskPiiAndGdprArt9ByDefault()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true, RequirePiiMasking = true }
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var guardrail = new AiDataGuardrailService(registry, options, logger);

        var session = new McpSessionContext("sess-1", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();

        // Email masked: erika.mustermann@acme-corp.com -> e***@acme-corp.com
        result.ContentJson.ShouldNotContain("erika.mustermann@acme-corp.com");
        result.ContentJson.ShouldContain("***@acme-corp.com");

        // IBAN masked: DE89 3704 ... -> **** **** **** 1234
        result.ContentJson.ShouldNotContain("DE89 3704 0044 0532 0130 00");
        result.ContentJson.ShouldContain("**** **** **** 1234");

        // GDPR Art. 9: HealthCondition -> [REDACTED-GDPR-ART9]
        result.ContentJson.ShouldNotContain("Diabetes Type 2");
        result.ContentJson.ShouldContain("[REDACTED-GDPR-ART9]");
    }

    [Fact]
    public async Task AiDataGuardrailService_WhenInsecureUnmaskedAllowed_ShouldNotMaskPii()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true, warn_allow_unmasked_ai_access = true }
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var guardrail = new AiDataGuardrailService(registry, options, logger);

        var session = new McpSessionContext("sess-2", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeFalse();
        result.ContentJson.ShouldContain("erika.mustermann@acme-corp.com");
        result.ContentJson.ShouldContain("Diabetes Type 2");
    }

    [Fact]
    public async Task AiDataGuardrailService_ShouldTruncateWhenExceedingTokenBudget()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = 30 } // Very small token budget
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var guardrail = new AiDataGuardrailService(registry, options, logger);

        var session = new McpSessionContext("sess-3", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.TruncatedDueToBudget.ShouldBeTrue();
        result.ContentJson.ShouldContain("[TRUNCATED DUE TO MCP TOKEN BUDGET]");
    }

    [Fact]
    public async Task McpProtocolHandler_LifecycleAndJsonRpcWorkflow_ShouldSucceed()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(registry, options, NullLogger<AiDataGuardrailService>.Instance);
        var sessionStore = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var handler = new McpProtocolHandler(sessionStore, registry, guardrail, NullLogger<McpProtocolHandler>.Instance);

        // 1. Session creation
        var session = handler.CreateSession("svc-ai-claude", "tenant-prod-1");
        session.SessionId.ShouldNotBeNullOrWhiteSpace();
        session.ServicePrincipalId.ShouldBe("svc-ai-claude");
        session.TenantId.ShouldBe("tenant-prod-1");

        // 2. JSON-RPC 'initialize'
        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var initResponse = await handler.HandleMessageAsync(session.SessionId, initPayload);
        using var initDoc = JsonDocument.Parse(initResponse);
        initDoc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2024-11-05");
        initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("Autheris.McpServer");

        // 3. JSON-RPC 'ping'
        var pingPayload = """{"jsonrpc":"2.0","id":2,"method":"ping"}""";
        var pingResponse = await handler.HandleMessageAsync(session.SessionId, pingPayload);
        using var pingDoc = JsonDocument.Parse(pingResponse);
        pingDoc.RootElement.GetProperty("id").GetInt64().ShouldBe(2);

        // 4. JSON-RPC 'tools/list'
        var toolsListPayload = """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""";
        var toolsListResponse = await handler.HandleMessageAsync(session.SessionId, toolsListPayload);
        using var toolsDoc = JsonDocument.Parse(toolsListResponse);
        var toolsArray = toolsDoc.RootElement.GetProperty("result").GetProperty("tools");
        toolsArray.GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);

        // 5. JSON-RPC 'tools/call'
        var toolCallPayload = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"query_invoices","arguments":{"currency":"EUR"}}}""";
        var toolCallResponse = await handler.HandleMessageAsync(session.SessionId, toolCallPayload);
        using var toolDoc = JsonDocument.Parse(toolCallResponse);
        var content = toolDoc.RootElement.GetProperty("result").GetProperty("content");
        content.GetArrayLength().ShouldBe(1);
        var textContent = content[0].GetProperty("text").GetString();
        textContent.ShouldNotBeNull();
        textContent.ShouldContain("INV-2026-001");

        // 6. Session termination
        handler.RemoveSession(session.SessionId).ShouldBeTrue();
        handler.GetSession(session.SessionId).ShouldBeNull();

        // 7. Call with terminated session fails
        var expiredResponse = await handler.HandleMessageAsync(session.SessionId, pingPayload);
        using var expDoc = JsonDocument.Parse(expiredResponse);
        expDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32000);
    }

    [Fact]
    public async Task AiDataGuardrailService_ShouldRecordAuditLogEntry_OnToolExecution()
    {
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            auditLogRepository: auditRepo);

        var session = new McpSessionContext("sess-audit-1", "agent-audit", "tenant-audit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "MCP_TOOL_EXECUTION" &&
                e.ActorSid == new Sid("agent-audit") &&
                e.TargetTable == "query_customers" &&
                e.Decision == "ALLOW"),
            Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task AiDataGuardrailService_WhenPolicyDenies_ShouldReturnErrorAndRecordDenyAudit()
    {
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var policyService = new DenyingPolicyService();

        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            auditLogRepository: auditRepo,
            policyEnforcementService: policyService);

        var session = new McpSessionContext("sess-deny-1", "agent-unauthorized", "tenant-test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("denied");

        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "MCP_TOOL_EXECUTION" &&
                e.Decision == "DENY"),
            Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task AiDataGuardrailService_WhenTargetTableRequiresFourEyes_ShouldReturnFourEyesErrorAndRecordDenyAudit()
    {
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();

        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new TableMetadata
            {
                Table = new Table
                {
                    Sensitivity = "HIGH",
                    RequiresFourEyes = true,
                    IsActive = true
                }
            });

        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            auditLogRepository: auditRepo,
            tableMetadataRepository: metadataRepo);

        var session = new McpSessionContext("sess-4eyes-1", "agent-claude", "tenant-test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Four-Eyes justification approval");

        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "MCP_TOOL_EXECUTION" &&
                e.Decision == "DENY" &&
                e.DetailsJson.Contains("Four-Eyes")),
            Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task McpSessionStore_RegisterAndSendSseEvent_ShouldDispatchEventToActiveStream()
    {
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var session = store.CreateSession("svc-ai", "tenant-1");

        string? receivedEvent = null;
        string? receivedData = null;

        store.RegisterSseSender(session.SessionId, (evt, data) =>
        {
            receivedEvent = evt;
            receivedData = data;
            return Task.CompletedTask;
        });

        var dispatched = await store.SendEventAsync(session.SessionId, "notifications/progress", """{"progress":50}""");
        dispatched.ShouldBeTrue();
        receivedEvent.ShouldBe("notifications/progress");
        receivedData.ShouldNotBeNull();
        receivedData.ShouldContain("50");

        // Terminating session cleans up sender
        store.RemoveSession(session.SessionId).ShouldBeTrue();
        var dispatchedAfterRemove = await store.SendEventAsync(session.SessionId, "notifications/progress", """{"progress":100}""");
        dispatchedAfterRemove.ShouldBeFalse();
    }

    private sealed class DenyingPolicyService : IPolicyEnforcementService
    {
        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, System.Threading.CancellationToken ct = default)
        {
            return ValueTask.FromResult(TableAccessDecision.Denied(
                context.TargetTable,
                $"Role 'AiAgent' not authorized for table '{context.TargetTable}'."));
        }

        public bool HasPolicies(TenantId tenant) => true;
        public Task ReloadPoliciesAsync(TenantId tenant, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public void LoadPolicyFromText(TenantId tenant, string policyText) { }
        public void LoadPolicyFromFile(TenantId tenant, string filePath, bool watchFile = false) { }
    }


    [Fact]
    public async Task AiDataGuardrailService_ShouldEmitGenAiOpenTelemetrySpanAndAttributes()
    {
        // Arrange
        System.Diagnostics.Activity? capturedActivity = null;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == Autheris.Application.Mcp.Diagnostics.McpDiagnostics.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = act => capturedActivity = act
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions());
        var queryExecutor = Substitute.For<IMcpQueryExecutor>();
        queryExecutor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult("""{"customers":[{"id":"1","name":"Alice"}]}"""));

        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            queryExecutor);

        var request = new McpToolCallRequest("query_customers", """{"limit":1}""");
        var session = new McpSessionContext("session-otel", "agent-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        capturedActivity.ShouldNotBeNull();
        capturedActivity.GetTagItem("gen_ai.system").ShouldBe("autheris_mcp");
        capturedActivity.GetTagItem("gen_ai.operation.name").ShouldBe("tool_execution");
        capturedActivity.GetTagItem("gen_ai.tool.name").ShouldBe("query_customers");
        capturedActivity.GetTagItem("gen_ai.guardrail.verdict").ShouldBe("allow");
    }
}
