namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>A guardrail over in-memory fakes for the error-envelope tests (no mocking library).</summary>
internal sealed class McpErrorTestRig
{
    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    public required AiDataGuardrailService Guardrail { get; init; }
    public required InMemoryAuditLogRepository Audit { get; init; }
    public required StubMcpQueryExecutor Executor { get; init; }

    public static TableIdentifier Table(string name) => new("sales", "public", name);

    public static TableMetadata Active(string name) =>
        new() { Identifier = Table(name), Table = new Table { TableName = name, IsActive = true } };

    public static McpSessionContext Session(string[]? roles = null, bool readOnly = false) =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: roles ?? ["Analyst"], IsReadOnly: readOnly);

    public static McpErrorTestRig Create(
        Func<McpToolDefinition, string, McpSessionContext, string>? execute = null,
        Func<AccessRequest, TableAccessDecision>? decide = null,
        IEnumerable<TableMetadata>? tables = null,
        GatewayOptions? options = null,
        IGraphQlCatalogMap? graphQlCatalogMap = null,
        IAccessPlanningService? planning = null)
    {
        options ??= new GatewayOptions { Mcp = new McpOptions { Enabled = true } };
        var switches = ReadPathSwitches.From(options);
        var registry = new McpToolRegistry(Options.Create(options), new NoDemo(), switches);
        var audit = new InMemoryAuditLogRepository();
        var executor = new StubMcpQueryExecutor(execute);
        var guardrail = new AiDataGuardrailService(
            registry, Options.Create(options), NullLogger<AiDataGuardrailService>.Instance, audit,
            queryExecutor: executor,
            decisionPoint: decide is null ? PdpTestDoubles.AllowAll() : PdpTestDoubles.From(decide),
            tableMetadataRepository: new InMemoryTableMetadataRepository([.. tables ?? []]),
            graphQlCatalogMap: graphQlCatalogMap,
            accessPlanningService: planning,
            switches: switches);
        return new McpErrorTestRig { Guardrail = guardrail, Audit = audit, Executor = executor };
    }

    public ValueTask<McpToolCallResult> Call(string tool, string argumentsJson, McpSessionContext? session = null) =>
        Guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(tool, argumentsJson), session ?? Session());

    public static TableAccessDecision Deny(AccessRequest request) => TableAccessDecision.Denied(request.Object.Identifier, "denied");
}

