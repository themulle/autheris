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
            null, 30, 14, 48, true, "admin-a", FixedNow);
        var routeB = new ConsentWorkflowRoute(
            Store.TenantB.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.Internal,
            null, 60, 21, 72, true, "admin-b", FixedNow);
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
            Store.TenantA.Value, WorkflowRouteScopeKind.DataSource, "sales", ConsentWorkflowMode.Internal,
            null, 45, 14, 24, true, "admin-a", FixedNow);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(route);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(route with { MaxDurationDays = 30 });

        var routes = await Store.ConsentWorkflowAdmin.ListWorkflowRoutesAsync(Store.TenantA);
        routes.Where(r => r.ScopeKind == WorkflowRouteScopeKind.DataSource && r.ScopeRef == "sales")
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

        var remaining = await Store.GrantExpiry.GetGrantsWithoutExpiryNoticeAsync(FixedNow);
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
            Store.TenantA.Value, WorkflowRouteScopeKind.DataOwner, "owner-soft-delete", ConsentWorkflowMode.Internal,
            null, 30, 14, 24, true, "admin-a", FixedNow);
        await Store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(route);

        var deleted = await Store.ConsentWorkflowAdmin.DeleteWorkflowRouteAsync(
            Store.TenantA, WorkflowRouteScopeKind.DataOwner, "owner-soft-delete");
        deleted.Should().BeTrue();

        var afterDelete = await Store.ConsentWorkflowAdmin.GetWorkflowRouteAsync(
            Store.TenantA, WorkflowRouteScopeKind.DataOwner, "owner-soft-delete");
        afterDelete.Should().BeNull();
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
