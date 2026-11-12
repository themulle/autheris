namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
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

    private static Harness Create(Func<TableIdentifier, TableAccessDecision> decide, Action<AccessRequest>? capture = null, bool knownTables = true, Func<TableIdentifier, TableMetadata>? metadataOf = null, CatalogOutputShape? shape = null, CatalogView view = CatalogView.Granted, bool released = false)
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capture?.Invoke(ci.Arg<AccessRequest>());
                return ValueTask.FromResult(decide(ci.Arg<AccessRequest>().Object.Identifier));
            });
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => knownTables
                ? (metadataOf?.Invoke(ci.Arg<TableIdentifier>()) ?? new TableMetadata { Identifier = ci.Arg<TableIdentifier>(), Table = new Table { IsActive = true } })
                : null);
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
            sqlEngine: new TrinoSqlEngine.FastSqlEngine(),
            catalogVisibility: Visibility(shape, view, released),
            eventBus: bus);
        return new Harness(guardrail, pdp, audit, bus, executor);
    }

    private static ICatalogVisibilityService Visibility(CatalogOutputShape? shape, CatalogView view, bool released)
    {
        var visibility = Substitute.For<ICatalogVisibilityService>();
        visibility.FindVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => shape is { } s
                ? new CatalogVisibleRow(new TableMetadata { Identifier = ci.Arg<TableIdentifier>(), Table = new Table { IsActive = true }, CatalogView = view, ReleaseProfileToCatalogViewers = released }, s)
                : (CatalogVisibleRow?)null);
        return visibility;
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

    [Fact]
    public void Constructor_requires_a_decision_point()
    {
        var ex = Should.Throw<ArgumentNullException>(() => new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            Substitute.For<IAuditLogRepository>(),
            decisionPoint: null));

        ex.ParamName.ShouldBe("decisionPoint");
    }

    [Fact]
    public async Task Sql_tool_naming_a_catalog_pseudo_table_still_reaches_the_decision_point()
    {
        // A user-named governance.catalog.t is a data target like any other; only the catalog tools skip the decision.
        var h = Create(Denied);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", """{"query":"SELECT * FROM governance.catalog.t"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        await h.Executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
        await h.Pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r => r.Object.Identifier.TableName == "t" && r.Transport == AccessTransport.Mcp),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tool_call_denied_when_catalog_has_no_metadata_for_the_table()
    {
        var h = Create(t => TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true), knownTables: false);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", """{"query":"{ sales_public_orders { id } }"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        await h.Pdp.DidNotReceiveWithAnyArgs().DecideAsync(default!, default);
        await h.Executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Degraded_group_snapshot_reaches_the_decision_point_as_degraded_without_groups()
    {
        AccessRequest? captured = null;
        var h = Create(t => TableAccessDecision.Denied(t, "degraded"), r => captured = r);
        var session = new McpSessionContext(
            "s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            UserSid: "S-1-USER", GroupSids: ["S-1-GROUP"], GroupsDegraded: true);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", """{"query":"{ sales_public_orders { id } }"}"""), session);

        result.IsSuccess.ShouldBeFalse();
        captured.ShouldNotBeNull();
        captured.Subject.GroupResolutionStatus.ShouldBe(Autheris.Domain.Identity.GroupResolutionStatus.Degraded);
        captured.Subject.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Decision_point_timeout_with_a_live_token_denies()
    {
        var h = Create(_ => throw new OperationCanceledException("pdp timeout"));

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", """{"query":"{ sales_public_orders { id } }"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        await h.Executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Describe_dataset_full_view_is_denied_by_the_decision_point_and_returns_nothing()
    {
        var h = Create(Denied, shape: CatalogOutputShape.Full, view: CatalogView.Granted);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, """{"dataset":"sales.public.orders"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ContentJson.ShouldBe("{}");
        await h.Executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
        await h.Pdp.Received(1).DecideAsync(
            Arg.Is<AccessRequest>(r => r.Object.Identifier == Orders && r.Transport == AccessTransport.Mcp && r.Operation == AccessOperation.Read),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Describe_dataset_summary_view_needs_no_grant()
    {
        var h = Create(Denied, shape: CatalogOutputShape.Summary);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, """{"dataset":"sales.public.orders"}"""), Session());

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        await h.Pdp.DidNotReceiveWithAnyArgs().DecideAsync(default!, default);
    }

    [Fact]
    public async Task Describe_dataset_brief_needs_no_grant_but_unknown_shape_is_decided()
    {
        var brief = Create(Denied, shape: CatalogOutputShape.Full);
        (await brief.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, """{"dataset":"sales.public.orders","brief":true}"""), Session()))
            .IsSuccess.ShouldBeTrue();
        await brief.Pdp.DidNotReceiveWithAnyArgs().DecideAsync(default!, default);

        var unknown = Create(Denied, knownTables: false);
        (await unknown.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, """{"dataset":"sales.public.orders"}"""), Session()))
            .IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Describe_dataset_tier_full_row_is_decided_only_when_profiles_are_released(bool released, bool expectDecision)
    {
        var h = Create(Denied, shape: CatalogOutputShape.Full, view: CatalogView.Full, released: released);

        var result = await h.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, """{"dataset":"sales.public.orders"}"""), Session());

        result.IsSuccess.ShouldBe(!expectDecision);
        if (expectDecision)
        {
            await h.Pdp.Received(1).DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await h.Pdp.DidNotReceiveWithAnyArgs().DecideAsync(default!, default);
        }
    }
}

