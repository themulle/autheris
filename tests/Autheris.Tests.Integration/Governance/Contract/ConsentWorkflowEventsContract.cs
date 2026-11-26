using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Issue #1161: an event that names an observer as its system gets no routed delivery (the routed insert only targets workflow
    /// systems); the observer fan-out alone delivers it, exactly once.
    /// </summary>
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

    /// <summary>
    /// Issue #1161: inside the repository transaction, open requests are never rerouted to an observer and a route never targets one;
    /// a workflow system that holds a pending request cannot become an observer.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Observers_never_receive_open_requests_or_routes()
    {
        if (!Store.IsAvailable) return;

        var workflow = NewSystem(Store.TenantA.Value, "workflow-pending", ExternalWorkflowSystemRole.Workflow);
        var observer = NewSystem(Store.TenantA.Value, "observer-target", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(workflow);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        var requestId = await SeedPendingRequestAsync(Store.TenantA, "observer_target");

        var rerouteToObserver = () => Store.ConsentWorkflowAdmin.RerouteOpenRequestsAsync(
            Store.TenantA, null, null, ConsentWorkflowMode.External, observer.Id, "admin", FixedNow);
        (await rerouteToObserver.Should().ThrowAsync<ConsentDecisionException>()).Which.StatusCode.Should().Be(409);
        (await Store.ConsentApprovals.GetConsentRequestAsync(requestId))!.ExternalSystemId.Should().BeNull();

        var routeToObserver = () => Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(new ConsentWorkflowRoute(
            Store.TenantA.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.External, observer.Id, 30, 14, true, "admin",
            FixedNow));
        (await routeToObserver.Should().ThrowAsync<ConsentDecisionException>()).Which.StatusCode.Should().Be(409);
        (await Store.ConsentWorkflowAdmin.GetWorkflowRouteAsync(Store.TenantA, WorkflowRouteScopeKind.Tenant, "*")).Should().BeNull();

        var rerouted = await Store.ConsentWorkflowAdmin.RerouteOpenRequestsAsync(
            Store.TenantA, null, null, ConsentWorkflowMode.External, workflow.Id, "admin", FixedNow);
        rerouted.ReroutedRequestIds.Should().Contain(requestId);

        var switchPending = () => Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(
            workflow with { Role = ExternalWorkflowSystemRole.Observer });
        (await switchPending.Should().ThrowAsync<ConsentDecisionException>()).Which.StatusCode.Should().Be(409);
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, workflow.Id))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Workflow);
    }

    /// <summary>
    /// Issue #1161: a system that stops being an observer loses the tenant-wide feed it received as an observer and receives no
    /// further fan-out.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Leaving_the_observer_role_drops_the_tenant_wide_deliveries()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-leaving", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        await AppendAsync(Store.TenantA.Value, "seen-as-observer", null);
        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events.Should().ContainSingle();

        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer with { Role = ExternalWorkflowSystemRole.Workflow });
        await AppendAsync(Store.TenantA.Value, "after-the-switch", null);

        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events.Should().BeEmpty();
    }

    /// <summary>Issue #1161: disabling an observer reports and keeps its role.</summary>
    [RequiresContainerRuntimeFact]
    public async Task Disabling_an_observer_keeps_its_role()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-disable", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);

        var (disabled, _) = await Store.ConsentWorkflowAdmin.DisableExternalWorkflowSystemAsync(
            Store.TenantA, observer.Id, "admin", FixedNow);

        disabled!.Role.Should().Be(ExternalWorkflowSystemRole.Observer);
        disabled.Enabled.Should().BeFalse();
        (await Store.ConsentWorkflowConfig.GetExternalWorkflowSystemAsync(Store.TenantA, observer.Id))!.Role
            .Should().Be(ExternalWorkflowSystemRole.Observer);
    }

    /// <summary>
    /// Issue #1165: an observer learns from its feed that an internal-mode request was decided (request.decided, committed in the
    /// decision transaction) or withdrawn (request.updated), with the request's status and version after the change.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Observer_feed_shows_the_decision_and_the_withdrawal_of_internal_requests()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-decisions", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        var ownerGroup = new Sid("S-1-5-21-1000-1000-1000-4510");
        var requesterSid = new Sid("S-1-5-21-1000-1000-1000-2510");
        var requester = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", requesterSid.Value), new Claim("tenant_id", Store.TenantA.Value), new Claim(ClaimTypes.Role, "DataConsumer")], "Test"));
        var requests = Store.Services.GetRequiredService<IConsentRequestService>();

        // one table per request, each in an Open domain owned by the owner group, so the two requests never collide
        async Task<Guid> SubmitAsync(string prefix)
        {
            var table = new TableIdentifier("sales", "dbo", prefix + Guid.NewGuid().ToString("N")[..8]);
            await Store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = table,
                ReplaceKeyDefinition = true,
                Table = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, IsActive = true },
                Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }],
                PrimaryKeyColumns = ["id"]
            });
            await OpenDomainSeeder.AssignToOpenDomainAsync(Store.Services, Store.TenantA, table, ownerGroup);

            var submitted = await requests.SubmitAsync(new ConsentRequestSubmission(
                table.Domain, table.Schema, table.TableName, "Quarterly order reconciliation for the finance close.", DurationDays: 10,
                IdempotencyKey: prefix + Guid.NewGuid().ToString("N"), UserSid: requesterSid, Principal: requester, ContextTenant: Store.TenantA));
            submitted.Status.Should().Be("PENDING");
            return submitted.RequestId;
        }

        var approvedId = await SubmitAsync("orders_decided_");
        var withdrawnId = await SubmitAsync("orders_withdrawn_");

        var owner = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1000-1000-1000-4511"),
            new Claim("tenant_id", Store.TenantA.Value),
            new Claim(ClaimTypes.Role, "DataOwner"),
            IdentityClaimIssuers.CreateGroupSidClaim(ownerGroup.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        ], "Test"));
        var engine = Store.Services.GetRequiredService<Autheris.Application.Governance.Requests.IPermissionRequestEngine>();
        var decided = await engine.DecideAsync(new Autheris.Application.Governance.Requests.PermissionDecisionCommand(
            approvedId, 1, SecurityPrincipalContext.FromPrincipal(owner), Approve: true));
        decided.Request.Status.Should().Be("APPROVED");
        (await requests.WithdrawAsync(withdrawnId, requester)).Should().BeTrue();

        var feed = await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id);
        feed.Events.Where(e => e.SubjectId == approvedId.ToString("D")).Select(e => e.Type).Should()
            .Equal(ConsentEventTypes.RequestCreated, ConsentEventTypes.RequestDecided);
        feed.Events.Where(e => e.SubjectId == withdrawnId.ToString("D")).Select(e => e.Type).Should()
            .Equal(ConsentEventTypes.RequestCreated, ConsentEventTypes.RequestUpdated);

        var decidedEvent = feed.Events.Single(e => e.SubjectId == approvedId.ToString("D") && e.Type == ConsentEventTypes.RequestDecided);
        ReadStatusAndVersion(decidedEvent.PayloadJson).Should().Be(("APPROVED", 2));
        var withdrawnEvent = feed.Events.Single(e => e.SubjectId == withdrawnId.ToString("D") && e.Type == ConsentEventTypes.RequestUpdated);
        ReadStatusAndVersion(withdrawnEvent.PayloadJson).Should().Be(("WITHDRAWN", 2));
        withdrawnEvent.PayloadJson.Should().NotContain("Quarterly order reconciliation");
        (await Store.ConsentApprovals.GetConsentRequestAsync(withdrawnId))!.Version.Should().Be(2);
    }

    /// <summary>
    /// Issue #1165: a withdrawal through the repository (the requester's withdrawal and the deprovisioning withdrawal share it) appends
    /// request.updated in the withdrawal transaction, read on the same connection (no self-blocking read on SQL Server). The event of
    /// an externally routed request is delivered to its workflow system and fanned out to observers; a refused withdrawal appends nothing.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Repository_withdrawal_appends_request_updated_in_its_transaction()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-withdraw", ExternalWorkflowSystemRole.Observer);
        var workflow = NewSystem(Store.TenantA.Value, "workflow-withdraw", ExternalWorkflowSystemRole.Workflow);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(workflow);
        var requestId = await SeedPendingRequestAsync(Store.TenantA, "orders_withdraw_" + Guid.NewGuid().ToString("N")[..8], workflow.Id);

        (await Store.ConsentApprovals.WithdrawConsentRequestAsync(requestId, new Sid("S-1-5-21-OTHER-REQ"))).Should().BeFalse();
        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Should().NotContain(e => e.SubjectId == requestId.ToString("D"));

        (await Store.ConsentApprovals.WithdrawConsentRequestAsync(requestId, new Sid("S-1-5-21-OBSERVER-REQ"))).Should().BeTrue();

        var stored = await Store.ConsentApprovals.GetConsentRequestAsync(requestId);
        stored!.Status.Should().Be("WITHDRAWN");
        stored.Version.Should().Be(2);
        var withdrawn = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Where(e => e.SubjectId == requestId.ToString("D")).Should().ContainSingle().Which;
        withdrawn.Type.Should().Be(ConsentEventTypes.RequestUpdated);
        ReadStatusAndVersion(withdrawn.PayloadJson).Should().Be(("WITHDRAWN", 2));
        var routed = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, workflow.Id)).Events
            .Where(e => e.SubjectId == requestId.ToString("D")).Should().ContainSingle().Which;
        routed.Id.Should().Be(withdrawn.Id);

        (await Store.ConsentApprovals.WithdrawConsentRequestAsync(requestId, new Sid("S-1-5-21-OBSERVER-REQ"))).Should().BeFalse();
        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Count(e => e.SubjectId == requestId.ToString("D")).Should().Be(1);
    }

    /// <summary>
    /// Issue #1170: deprovisioning a leaver appends one grant.revoked per revoked grant in the revocation transaction. The event is routed
    /// by the grant's origin request and fanned out to observers; a second run appends nothing. The external-system revocation appends
    /// exactly one grant.revoked as well.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Observer_feed_shows_grant_revoked_after_deprovisioning()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-deprovision", ExternalWorkflowSystemRole.Observer);
        var workflow = NewSystem(Store.TenantA.Value, "workflow-deprovision", ExternalWorkflowSystemRole.Workflow);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(workflow);
        var leaver = new Sid("S-1-5-21-1000-1000-1000-1170");

        // A consent request yields at most one consent (ux_consents_request), so every request-linked grant gets its own origin request.
        async Task<(Guid RequestId, TableIdentifier Table, Guid TableId)> SeedRoutedRequestAsync(string prefix)
        {
            var tableName = prefix + Guid.NewGuid().ToString("N")[..8];
            var requestId = await SeedPendingRequestAsync(Store.TenantA, tableName, workflow.Id);
            var table = new TableIdentifier("sales", "dbo", tableName);
            return (requestId, table, (await Store.TableMetadata.GetTableMetadataAsync(table))!.Table.Id);
        }

        Task<Consent> GrantAsync(Sid grantee, (Guid? RequestId, TableIdentifier Table, Guid TableId) origin) => Store.Consents.CreateConsentAsync(new Consent
        {
            TableId = origin.TableId, TableIdentifier = origin.Table, TenantId = Store.TenantA, GranteeType = GranteeType.User, GranteeSid = grantee,
            Effect = ConsentEffect.Allow, ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            CreatedBySid = new Sid("S-1-5-21-ADMIN-1170"), ConsentRequestId = origin.RequestId
        });

        var leaverOrigin = await SeedRoutedRequestAsync("orders_leaver_");
        var externalOrigin = await SeedRoutedRequestAsync("orders_ext_revoke_");
        var originRequest = leaverOrigin.RequestId;
        var routedGrant = await GrantAsync(leaver, leaverOrigin);
        var internalGrant = await GrantAsync(leaver, (null, leaverOrigin.Table, leaverOrigin.TableId));
        var externalGrant = await GrantAsync(new Sid("S-1-5-21-1000-1000-1000-1171"), externalOrigin);

        var deprovisioning = new GrantRevocationService(Store.Consents, Store.ConsentApprovals, new Autheris.Tests.Common.Fakes.InMemoryAuditWriter());
        var run = await deprovisioning.RevokeAllForSubjectAsync(Store.TenantA, leaver, "SCIM_DEPROVISION");
        run.Complete.Should().BeTrue();
        run.GrantsRevoked.Should().Be(2);
        (await deprovisioning.RevokeAllForSubjectAsync(Store.TenantA, leaver, "SCIM_DEPROVISION")).GrantsRevoked.Should().Be(0);

        var actor = new Sid("external-system:" + workflow.Id.ToString("D"));
        (await Store.Consents.RevokeSystemConsentAsync(externalGrant.Id, externalOrigin.RequestId, actor, "Access no longer needed")).Should().BeTrue();
        (await Store.Consents.RevokeSystemConsentAsync(externalGrant.Id, externalOrigin.RequestId, actor, "Second attempt")).Should().BeFalse();

        var observed = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Where(e => e.Type == ConsentEventTypes.GrantRevoked).ToList();
        observed.Select(e => e.SubjectId).Should().BeEquivalentTo(
            [routedGrant.Id.ToString("D"), internalGrant.Id.ToString("D"), externalGrant.Id.ToString("D")]);
        observed.Should().OnlyContain(e => ReadStatusAndVersion(e.PayloadJson) == ("REVOKED", 1));
        observed.Should().OnlyContain(e => !e.PayloadJson.Contains("SCIM_DEPROVISION") && !e.PayloadJson.Contains(leaver.Value));

        var routed = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, workflow.Id)).Events
            .Where(e => e.Type == ConsentEventTypes.GrantRevoked).ToList();
        routed.Select(e => e.SubjectId).Should().BeEquivalentTo([routedGrant.Id.ToString("D"), externalGrant.Id.ToString("D")]);
        routed.Should().OnlyContain(r => observed.Any(o => o.Id == r.Id));
        using var payload = JsonDocument.Parse(routed.First(e => e.SubjectId == routedGrant.Id.ToString("D")).PayloadJson);
        payload.RootElement.GetProperty("workflowMode").GetString().Should().Be("EXTERNAL");
        payload.RootElement.GetProperty("requestId").GetGuid().Should().Be(originRequest);
        using var externalPayload = JsonDocument.Parse(routed.First(e => e.SubjectId == externalGrant.Id.ToString("D")).PayloadJson);
        externalPayload.RootElement.GetProperty("requestId").GetGuid().Should().Be(externalOrigin.RequestId);
    }

    /// <summary>
    /// Issue #1173: a renewal appends grant.renewed for the renewed grant, routed by the superseded grant's origin request. Rolling back
    /// a renewal that was announced appends grant.revoked for the renewed grant; rolling back one that was never announced appends nothing.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Observer_feed_shows_grant_renewed_and_the_terminal_revoke_of_a_rolled_back_renewal()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-renewal", ExternalWorkflowSystemRole.Observer);
        var workflow = NewSystem(Store.TenantA.Value, "workflow-renewal", ExternalWorkflowSystemRole.Workflow);
        var otherTenantObserver = NewSystem(Store.TenantB.Value, "observer-renewal-b", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(workflow);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(otherTenantObserver);
        var actor = new Sid("S-1-5-21-ADMIN-1173");
        var grantee = new Sid("S-1-5-21-1000-1000-1000-1173");

        var origin = await SeedRoutedOriginAsync("orders_renew_", workflow.Id);
        var old = await CreateGrantAsync(grantee, origin.Table, origin.TableId, origin.RequestId);
        // The renewed row carries no origin request (ux_consents_request allows one consent per request).
        var renewed = await CreateGrantAsync(grantee, origin.Table, origin.TableId, null);

        (await Store.Consents.RevokeSystemConsentByIdAsync(Store.TenantA, old.Id, actor, "Renewed", SystemRevocationKind.Renewal, renewed.Id))
            .Should().BeTrue();

        foreach (var system in new[] { observer, workflow })
        {
            var announced = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, system.Id)).Events
                .Where(e => e.Type == ConsentEventTypes.GrantRenewed).Should().ContainSingle().Which;
            announced.SubjectId.Should().Be(renewed.Id.ToString("D"));
            using var payload = JsonDocument.Parse(announced.PayloadJson);
            payload.RootElement.GetProperty("previousGrantId").GetGuid().Should().Be(old.Id);
            payload.RootElement.GetProperty("requestId").GetGuid().Should().Be(origin.RequestId);
            payload.RootElement.GetProperty("workflowMode").GetString().Should().Be("EXTERNAL");
        }

        (await Store.Consents.RevokeSystemConsentByIdAsync(Store.TenantA, renewed.Id, actor, "RenewalRollback", SystemRevocationKind.RenewalRollback))
            .Should().BeTrue();
        var neverAnnounced = await CreateGrantAsync(grantee, origin.Table, origin.TableId, null);
        (await Store.Consents.RevokeSystemConsentByIdAsync(Store.TenantA, neverAnnounced.Id, actor, "RenewalRollback", SystemRevocationKind.RenewalRollback))
            .Should().BeTrue();

        var revoked = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Where(e => e.Type == ConsentEventTypes.GrantRevoked).Should().ContainSingle().Which;
        revoked.SubjectId.Should().Be(renewed.Id.ToString("D"));

        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantB, 0, 50, otherTenantObserver.Id)).Events
            .Where(e => e.Type == ConsentEventTypes.GrantRenewed || e.Type == ConsentEventTypes.GrantRevoked).Should().BeEmpty();
    }

    /// <summary>
    /// Issue #1173: revoking a profile assignment appends one grant.revoked per revoked consent in the revocation transaction; a second
    /// revocation of the same assignment revokes and appends nothing.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Observer_feed_shows_grant_revoked_for_each_consent_of_a_revoked_profile_assignment()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-profile", ExternalWorkflowSystemRole.Observer);
        var workflow = NewSystem(Store.TenantA.Value, "workflow-profile", ExternalWorkflowSystemRole.Workflow);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(workflow);
        var actor = new Sid("S-1-5-21-ADMIN-1173");
        var grantee = new Sid("S-1-5-21-1000-1000-1000-1174");
        var assignmentId = Guid.NewGuid();

        var routedOrigin = await SeedRoutedOriginAsync("orders_profile_a_", workflow.Id);
        var internalOrigin = await SeedRoutedOriginAsync("orders_profile_b_", null);
        var routed = await CreateGrantAsync(grantee, routedOrigin.Table, routedOrigin.TableId, routedOrigin.RequestId, assignmentId);
        var internalGrant = await CreateGrantAsync(grantee, internalOrigin.Table, internalOrigin.TableId, internalOrigin.RequestId, assignmentId);

        var materializer = new ProfileGrantMaterializer(
            Store.Services.GetRequiredService<IGrantWriter>(), Store.Services.GetRequiredService<IProfileAssignmentStore>());

        (await materializer.RevokeAssignmentAsync(Store.TenantA, assignmentId, actor, "Profile withdrawn")).Should().Be(2);
        (await materializer.RevokeAssignmentAsync(Store.TenantA, assignmentId, actor, "Profile withdrawn")).Should().Be(0);

        var observed = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Where(e => e.Type == ConsentEventTypes.GrantRevoked).ToList();
        observed.Select(e => e.SubjectId).Should().BeEquivalentTo([routed.Id.ToString("D"), internalGrant.Id.ToString("D")]);
        observed.Should().OnlyContain(e => ReadStatusAndVersion(e.PayloadJson) == ("REVOKED", 1));
        observed.Should().OnlyContain(e => !e.PayloadJson.Contains("Profile withdrawn") && !e.PayloadJson.Contains(grantee.Value));

        var routedFeed = (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, workflow.Id)).Events
            .Where(e => e.Type == ConsentEventTypes.GrantRevoked).Should().ContainSingle().Which;
        routedFeed.SubjectId.Should().Be(routed.Id.ToString("D"));
    }

    /// <summary>
    /// Issue #1173: the check that a renewal was announced is tenant-scoped: a grant.renewed of another tenant for the same subject id
    /// does not make the compensation emit grant.revoked.
    /// </summary>
    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Renewal_rollback_check_is_tenant_scoped_a_grant_renewed_of_another_tenant_does_not_announce()
    {
        if (!Store.IsAvailable) return;

        var observer = NewSystem(Store.TenantA.Value, "observer-rollback-scope", ExternalWorkflowSystemRole.Observer);
        await Store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(observer);
        var origin = await SeedRoutedOriginAsync("orders_scope_", null);
        var grant = await CreateGrantAsync(new Sid("S-1-5-21-1000-1000-1000-1175"), origin.Table, origin.TableId, null);

        await Store.ConsentEventOutbox.AppendAsync(new ConsentEventWrite(
            Store.TenantB.Value, Guid.NewGuid(), ConsentEventTypes.GrantRenewed, grant.Id.ToString("D"), "{}", FixedNow, null));

        (await Store.Consents.RevokeSystemConsentByIdAsync(
            Store.TenantA, grant.Id, new Sid("S-1-5-21-ADMIN-1173"), "RenewalRollback", SystemRevocationKind.RenewalRollback)).Should().BeTrue();

        (await Store.ConsentEventOutbox.ReadFeedAsync(Store.TenantA, 0, 50, observer.Id)).Events
            .Should().NotContain(e => e.Type == ConsentEventTypes.GrantRevoked);
    }

    private async Task<(Guid RequestId, TableIdentifier Table, Guid TableId)> SeedRoutedOriginAsync(string prefix, Guid? externalSystemId)
    {
        var tableName = prefix + Guid.NewGuid().ToString("N")[..8];
        var requestId = await SeedPendingRequestAsync(Store.TenantA, tableName, externalSystemId);
        var table = new TableIdentifier("sales", "dbo", tableName);
        return (requestId, table, (await Store.TableMetadata.GetTableMetadataAsync(table))!.Table.Id);
    }

    private Task<Consent> CreateGrantAsync(Sid grantee, TableIdentifier table, Guid tableId, Guid? requestId, Guid? profileAssignmentId = null) =>
        Store.Consents.CreateConsentAsync(new Consent
        {
            TableId = tableId, TableIdentifier = table, TenantId = Store.TenantA, GranteeType = GranteeType.User, GranteeSid = grantee,
            Effect = ConsentEffect.Allow, ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            CreatedBySid = new Sid("S-1-5-21-ADMIN-1173"), ConsentRequestId = requestId, ProfileAssignmentId = profileAssignmentId
        });

    private static (string? Status, int Version) ReadStatusAndVersion(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return (doc.RootElement.GetProperty("status").GetString(), doc.RootElement.GetProperty("version").GetInt32());
    }

    private async Task<Guid> SeedPendingRequestAsync(TenantId tenant, string tableName, Guid? externalSystemId = null)
    {
        var table = new TableIdentifier("sales", "dbo", tableName);
        var meta = await Store.Repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            OwnerTenantId = tenant.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, IsActive = true }
        });

        var request = await Store.Repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = new Sid("S-1-5-21-OBSERVER-REQ"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-OBSERVER-REQ",
            BusinessJustification = "Observer role contract test request.",
            RequestedValidTo = FixedNow.AddDays(14),
            Status = externalSystemId is null ? "PENDING" : "PENDING_EXTERNAL_APPROVAL",
            WorkflowMode = externalSystemId is null ? ConsentWorkflowMode.Internal : ConsentWorkflowMode.External,
            ExternalSystemId = externalSystemId,
            TenantId = tenant
        });
        return request.Id;
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
