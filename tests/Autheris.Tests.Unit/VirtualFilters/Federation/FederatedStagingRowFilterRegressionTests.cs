namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-1 (SEC-2): row filters of HTTP tables are always enforced by the gateway in a WebSQL cross-source query.
/// Neither the planner nor the connector can switch the in-memory row filter off; a Virtual Filter predicate that the
/// gateway cannot enforce on an HTTP table denies (pre-F-GOV-14 behaviour, fail closed).
/// </summary>
public sealed class FederatedStagingRowFilterRegressionTests
{
    private static readonly FastSqlEngine Engine = new();

    private static readonly TableMetadata Tickets = HttpTable("crm_api", "public", "tickets", "id", "status");

    private static IReadOnlyDictionary<string, object?>[] TicketRows() =>
    [
        Row(("id", "1"), ("status", "OPEN")),
        Row(("id", "2"), ("status", "CLOSED")),
        Row(("id", "3"), ("status", "OPEN"))
    ];

    private static async Task<IReadOnlyList<Autheris.Application.Olap.OlapTableSource>> PlanAndStageAsync(
        TableAccessDecision decision, StubTableConnector stub)
    {
        const string sql = "SELECT id, status FROM crm_api.public.tickets";
        var metadata = Engine.Analyze(sql.AsMemory());
        var resolved = new ResolvedSourceTable(metadata.ReferencedTables[0], Tickets.Identifier, Tickets, "crm_api");
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision> { [Tickets.Identifier] = decision };

        var plan = new CrossSourcePlanner(Engine).Plan(sql, metadata, [resolved], decisions, new CrossSourceOptions { Enabled = true });

        return await Staging(StubRegistry.For((Tickets, stub))).StageAsync(plan.StagingRequests, User(), Tenant, Budget());
    }

    [Fact]
    public async Task Stage_HttpTableWithConsentRowFilter_RowFilterIsEnforced()
    {
        var stub = new StubTableConnector("crm_api", TicketRows());

        var staged = await PlanAndStageAsync(Unrestricted(Tickets, "status = 'OPEN'"), stub);

        stub.UpstreamCalls.ShouldBe(1);
        var rows = staged.ShouldHaveSingleItem().GovernedRows;
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => (string?)r["status"] == "OPEN");
    }

    [Fact]
    public async Task Stage_ConnectorSelfAttestsFiltering_RowFilterIsStillEnforced()
    {
        // ST-4: a connector cannot switch the gateway row filter off by setting session flags while it reads.
        var stub = new StubTableConnector("crm_api", TicketRows(), session =>
        {
            session.Items["VirtualFilterFederationHandled"] = true;
            session.Items[GovernedConnectorReader.RlsPushdownExecutedKey] = true;
        });

        var staged = await PlanAndStageAsync(Unrestricted(Tickets, "status = 'OPEN'"), stub);

        var rows = staged.ShouldHaveSingleItem().GovernedRows;
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => (string?)r["status"] == "OPEN");
    }

    [Fact]
    public async Task Stage_HttpTableWithVirtualFilterPredicate_IsForbidden()
    {
        // ST-5: without gateway-side enforcement a Virtual Filter on an HTTP table is denied (HTTP 403), never ignored.
        var stub = new StubTableConnector("crm_api", TicketRows());
        var decision = Unrestricted(Tickets).WithMandatoryPredicate("id IN (SELECT key FROM vf)", ["vf_customers"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => PlanAndStageAsync(decision, stub));

        ex.ShouldBeOfType<GatewayForbiddenException>();
    }

    [Fact]
    public void Plan_HttpAndVirtualFilterTables_SetNoRowFilterSuppressionItem()
    {
        var orders = SqlTable("erp", "public", "orders", "id", "ticket_id");
        const string sql = "SELECT t.id, o.id FROM crm_api.public.tickets t JOIN erp.public.orders o ON o.ticket_id = t.id";
        var metadata = Engine.Analyze(sql.AsMemory());
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [Tickets.Identifier] = Unrestricted(Tickets),
            [orders.Identifier] = Unrestricted(orders).WithMandatoryPredicate("ticket_id IN (1)", ["vf_orders"])
        };

        var plan = new CrossSourcePlanner(Engine).Plan(
            sql,
            metadata,
            [
                new ResolvedSourceTable(metadata.ReferencedTables[0], Tickets.Identifier, Tickets, "crm_api"),
                new ResolvedSourceTable(metadata.ReferencedTables[1], orders.Identifier, orders, "erp")
            ],
            decisions,
            new CrossSourceOptions { Enabled = true });

        plan.StagingRequests.Count.ShouldBe(2);
        foreach (var request in plan.StagingRequests)
        {
            var items = request.SessionItems ?? new Dictionary<string, object?>();
            items.ShouldNotContainKey("VirtualFilterFederationHandled");
            items.ShouldNotContainKey(GovernedConnectorReader.RlsPushdownExecutedKey);
            items.ShouldNotContainKey(GovernedConnectorReader.InDbColumnMaskingExecutedKey);
        }
    }
}
