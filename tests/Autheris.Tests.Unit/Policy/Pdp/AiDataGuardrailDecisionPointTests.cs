namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Events;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Events;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-SIMPL-46 step 5: the MCP guardrail asks the one decision point for every disclosed table (SEC-AZ-01, SEC-AZ-10).</summary>
[Trait("Category", "Security")]
public sealed class AiDataGuardrailDecisionPointTests
{
    private const string Secret = "region = 'EU-secret'";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Customers = new("crm", "dbo", "customers");

    private sealed class FixedDemoData : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static TableAccessDecision Denied(TableIdentifier table) =>
        TableAccessDecision.Denied(table, Secret) with
        {
            Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "DEFAULT_DENY", null, null)]
        };

    private sealed record Harness(
        AiDataGuardrailService Guardrail,
        IAccessDecisionPoint Pdp,
        IAuditLogRepository Audit,
        IGatewayEventBus Bus,
        IMcpQueryExecutor Executor);

    private static Harness Create(Func<TableIdentifier, TableAccessDecision> decide)
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(decide(ci.Arg<AccessRequest>().Object.Identifier)));
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata { Identifier = ci.Arg<TableIdentifier>(), Table = new Table { IsActive = true } });
        var map = Substitute.For<IGraphQlCatalogMap>();
        map.ResolveDocumentTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableIdentifier>?>([Orders]));
        var audit = Substitute.For<IAuditLogRepository>();
        var bus = Substitute.For<IGatewayEventBus>();
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"data":{}}"""));

        // Casbin stays disabled (the default): the decision point check runs regardless.
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            audit,
            queryExecutor: executor,
            decisionPoint: pdp,
            tableMetadataRepository: tables,
            graphQlCatalogMap: map,
            eventBus: bus);
        return new Harness(guardrail, pdp, audit, bus, executor);
    }

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

    [Fact]
    public async Task Tool_call_denied_when_decision_point_denies()
    {
        var h = Create(Denied);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", """{"query":"{ sales_public_orders { id } }"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        await h.Executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
        await h.Pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r => r.Transport == AccessTransport.Mcp && r.Operation == AccessOperation.Read && r.Object.Identifier == Orders),
            Arg.Any<CancellationToken>());
        // Audited with reason codes only: neither the policy text nor a predicate value reaches the audit trail.
        await h.Audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "DENY" && e.DetailsJson!.Contains("DEFAULT_DENY") && !e.DetailsJson.Contains("EU-secret")),
            Arg.Any<CancellationToken>());
        await h.Bus.Received(1).PublishAsync(Arg.Any<PolicyDeniedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tool_call_allowed_when_decision_point_allows()
    {
        var h = Create(t => TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", """{"query":"{ sales_public_orders { id } }"}"""), Session());

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task Tool_call_denied_when_decision_point_throws()
    {
        var h = Create(_ => throw new InvalidOperationException("boom"));

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", """{"query":"{ sales_public_orders { id } }"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        await h.Executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Resource_read_denied_when_decision_point_denies_any_disclosed_table()
    {
        var h = Create(t => t == Customers
            ? Denied(t)
            : TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var read = false;

        var result = await h.Guardrail.ReadResourceWithGuardrailAsync(
            "autheris://semantic/sales",
            [Orders, Customers],
            _ =>
            {
                read = true;
                return Task.FromResult("{}");
            },
            Session());

        result.IsSuccess.ShouldBeFalse();
        read.ShouldBeFalse();
        await h.Audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "DENY" && e.DetailsJson!.Contains("DEFAULT_DENY") && !e.DetailsJson.Contains("EU-secret")),
            Arg.Any<CancellationToken>());
    }
}
