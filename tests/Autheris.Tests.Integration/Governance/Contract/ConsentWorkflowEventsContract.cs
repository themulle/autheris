using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using FluentAssertions;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Engine-agnostic contract suite for <c>IConsentWorkflowConfigRepository</c>,
/// <c>IConsentWorkflowAdminRepository</c>, <c>IConsentEventOutbox</c>, and <c>IGrantExpiryStore</c>.
/// </summary>
public abstract class ConsentWorkflowEventsContract : GovernanceContractBase
{
    private static readonly TableIdentifier TableOrders = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var routeA = new ConsentWorkflowRoute(
            Store.TenantA.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.Internal,
            null, 30, 14, true, "admin-a", FixedNow);
        var routeB = new ConsentWorkflowRoute(
            Store.TenantB.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.Internal,
            null, 60, 21, true, "admin-b", FixedNow);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(routeA);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(routeB);

        var routesA = await Store.ConsentWorkflowAdmin.ListWorkflowRoutesAsync(Store.TenantA);
        routesA.Should().ContainSingle().Which.TenantId.Should().Be(Store.TenantA.Value);

        var resolvedB = await Store.ConsentWorkflowConfig.ResolveWorkflowRouteAsync(Store.TenantB, TableOrders);
        resolvedB.Should().NotBeNull();
        resolvedB!.MaxDurationDays.Should().Be(60);

        await Store.ConsentEventOutbox.AppendAsync(new ConsentEventWrite(
            Store.TenantA.Value, Guid.NewGuid(), ConsentEventTypes.RequestCreated, "req-a", "{}", FixedNow, null));
        await Store.ConsentEventOutbox.AppendAsync(new ConsentEventWrite(
            Store.TenantB.Value, Guid.NewGuid(), ConsentEventTypes.RequestCreated, "req-b", "{}", FixedNow, null));

        var feedA = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50);
        feedA.Events.Should().NotBeEmpty();
        feedA.Events.Should().OnlyContain(e => e.TenantId == Store.TenantA.Value);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var systemId = Guid.NewGuid();
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(new ExternalWorkflowSystem(
            systemId, Store.TenantA.Value, "ServiceNow-A", "client-a",
            "https://workflow-a.example.com/hook", "protected-a", true, "admin-a", FixedNow, null));

        var hijackAttempt = () => Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(new ExternalWorkflowSystem(
            systemId, Store.TenantB.Value, "Hijacked", "client-b",
            "https://workflow-b.example.com/hook", "protected-b", true, "admin-b", FixedNow, null));
        await hijackAttempt.Should().ThrowAsync<UnauthorizedAccessException>();

        var storedA = await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, systemId);
        storedA.Should().NotBeNull();
        storedA!.Name.Should().Be("ServiceNow-A");
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantB, systemId)).Should().BeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        var listMissing = () => Store.ConsentWorkflowAdmin.ListWorkflowRoutesAsync(default);
        await listMissing.Should().ThrowAsync<ArgumentException>();

        var feedMissing = () => Store.ConsentEventOutbox.ReadFeedAsync(default, 0, 10);
        await feedMissing.Should().ThrowAsync<ArgumentException>();

        var appendMissing = () => Store.ConsentEventOutbox.AppendAsync(new ConsentEventWrite(
            string.Empty, Guid.NewGuid(), ConsentEventTypes.RequestCreated, "sub-1", "{}", FixedNow, null));
        await appendMissing.Should().ThrowAsync<Autheris.Domain.Exceptions.StoredTenantIntegrityException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var route = new ConsentWorkflowRoute(
            Store.TenantA.Value, WorkflowRouteScopeKind.Domain, "sales", ConsentWorkflowMode.Internal,
            null, 45, 14, true, "admin-a", FixedNow);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(route);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(route with { MaxDurationDays = 30 });

        var routes = await Store.ConsentWorkflowAdmin.ListWorkflowRoutesAsync(Store.TenantA);
        routes.Where(r => r.ScopeKind == WorkflowRouteScopeKind.Domain && r.ScopeRef == "sales")
            .Should().ContainSingle()
            .Which.MaxDurationDays.Should().Be(30);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var systemId = Guid.NewGuid();
        var original = new ExternalWorkflowSystem(
            systemId, Store.TenantA.Value, "Sys-Owner-A", "client-owner-a",
            "https://owner-a.example.com/hook", "secret-a", true, "admin-a", FixedNow, null);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(original);

        var crossTenant = () => Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(
            original with { TenantId = Store.TenantB.Value, Name = "Conflicting-Tenant" });
        await crossTenant.Should().ThrowAsync<UnauthorizedAccessException>();

        var remaining = await Store.GrantExpiry.GetGrantsWithPendingReminderAsync(FixedNow, FixedNow.AddDays(30), 10);
        remaining.Should().NotBeNull();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var seqs = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var seq = await Store.ConsentEventOutbox.AppendAsync(new ConsentEventWrite(
                Store.TenantA.Value,
                Guid.NewGuid(),
                ConsentEventTypes.RequestUpdated,
                "req-" + i,
                "{}",
                FixedNow.AddSeconds(i),
                null));
            seqs.Add(seq);
        }

        seqs.Should().BeInAscendingOrder();
        var page1 = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 2);
        page1.Events.Should().HaveCount(2);
        var page2 = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, page1.NextSince, 2);
        page2.Events.Select(e => e.Seq).Should().OnlyContain(s => s > page1.NextSince);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        var page = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50_000);
        page.Events.Count.Should().BeLessOrEqualTo(200);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var route = new ConsentWorkflowRoute(
            Store.TenantA.Value, WorkflowRouteScopeKind.Domain, "owner-soft-delete", ConsentWorkflowMode.Internal,
            null, 30, 14, true, "admin-a", FixedNow);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(route);

        var deleted = await Store.ConsentWorkflowAdmin.DeleteWorkflowRouteAsync(
            Store.TenantA, WorkflowRouteScopeKind.Domain, "owner-soft-delete");
        deleted.Should().BeTrue();

        var afterDelete = await Store.ConsentWorkflowAdmin.GetWorkflowRouteAsync(
            Store.TenantA, WorkflowRouteScopeKind.Domain, "owner-soft-delete");
        afterDelete.Should().BeNull();
    }

    private static ExternalWorkflowSystem NewSystem(string tenant, string clientId, ExternalWorkflowSystemRole role, bool enabled = true) =>
        new(Guid.NewGuid(), tenant, clientId, clientId, $"https://{clientId}.example.com/hook", "protected", enabled, "admin",
            FixedNow, enabled ? null : FixedNow, role);

    private Task<long> AppendAsync(string tenant, string subject, Guid? routedSystem) =>
        Store.ConsentEventOutbox.AppendAsync(new ConsentEventWrite(
            tenant, Guid.NewGuid(), ConsentEventTypes.RequestCreated, subject, "{}", FixedNow, routedSystem));

    /// <summary>Issue #1161: the stored role round-trips; systems saved without a role stay workflow systems.</summary>
    [RequiresContainerRuntimeFact]
    public async Task External_system_role_round_trips()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-rt", ExternalWorkflowSystemRole.Observer);
        var workflow = new ExternalWorkflowSystem(
            Guid.NewGuid(), Store.TenantA.Value, "workflow-rt", "workflow-rt", "https://workflow-rt.example.com/hook", "protected", true,
            "admin", FixedNow, null);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(workflow);

        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, observer.Id))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Observer);
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemByClientIdAsync(Store.TenantA, "observer-rt"))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Observer);
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, workflow.Id))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Workflow);
    }

    /// <summary>
    /// Issue #1161: every enabled observer of the event's tenant gets a delivery, for unrouted and routed events alike; the routed
    /// workflow system keeps receiving only its own events; observers of another tenant and disabled observers receive nothing.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Observers_receive_every_event_of_their_tenant_only()
    {
        if (!Store.IsAvailable) return;

        var observerA = NewSystem(Store.TenantA.Value, "observer-a", ExternalWorkflowSystemRole.Observer);
        var disabledObserverA = NewSystem(Store.TenantA.Value, "observer-a-off", ExternalWorkflowSystemRole.Observer, enabled: false);
        var workflowA = NewSystem(Store.TenantA.Value, "workflow-a", ExternalWorkflowSystemRole.Workflow);
        var observerB = NewSystem(Store.TenantB.Value, "observer-b", ExternalWorkflowSystemRole.Observer);
        foreach (var system in new[] { observerA, disabledObserverA, workflowA, observerB })
        {
            await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(system);
        }

        await AppendAsync(Store.TenantA.Value, "internal-request", null);
        await AppendAsync(Store.TenantA.Value, "routed-request", workflowA.Id);
        await AppendAsync(Store.TenantB.Value, "other-tenant-request", null);

        var observerFeed = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observerA.Id);
        observerFeed.Events.Select(e => e.SubjectId).Should().BeEquivalentTo(["internal-request", "routed-request"]);

        var workflowFeed = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, workflowA.Id);
        workflowFeed.Events.Select(e => e.SubjectId).Should().BeEquivalentTo(["routed-request"]);

        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, disabledObserverA.Id)).Events.Should().BeEmpty();
        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observerB.Id)).Events.Should().BeEmpty();
        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantB, 0, 50, observerB.Id)).Events
            .Select(e => e.SubjectId).Should().BeEquivalentTo(["other-tenant-request"]);
    }

    /// <summary>Issue #1161: an event that names an observer as its system is still delivered to that observer exactly once.</summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task An_event_naming_an_observer_is_delivered_once()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-once", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);

        await AppendAsync(Store.TenantA.Value, "named-observer", observer.Id);

        var feed = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id);
        feed.Events.Should().ContainSingle().Which.SubjectId.Should().Be("named-observer");
    }

    /// <summary>Issue #1161: a workflow system referenced by a route cannot become an observer; an unreferenced one can.</summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Referenced_workflow_system_cannot_become_an_observer()
    {
        if (!Store.IsAvailable) return;

        var referenced = NewSystem(Store.TenantA.Value, "workflow-routed", ExternalWorkflowSystemRole.Workflow);
        var free = NewSystem(Store.TenantA.Value, "workflow-free", ExternalWorkflowSystemRole.Workflow);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(referenced);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(free);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(new ConsentWorkflowRoute(
            Store.TenantA.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.External, referenced.Id, 30, 14, true, "admin",
            FixedNow));

        var switchReferenced = () => Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(
            referenced with { Role = ExternalWorkflowSystemRole.Observer });
        (await switchReferenced.Should().ThrowAsync<ConsentDecisionException>())
            .Which.StatusCode.Should().Be(409);
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, referenced.Id))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Workflow);

        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(free with { Role = ExternalWorkflowSystemRole.Observer });
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, free.Id))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Observer);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlConsentWorkflowEventsContractTests(GovernancePostgresFixture fixture)
    : ConsentWorkflowEventsContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "consent_workflow_events_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerConsentWorkflowEventsContractTests(GovernanceSqlServerFixture fixture)
    : ConsentWorkflowEventsContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "consent_workflow_events_contract");
}
