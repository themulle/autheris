using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Identity;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Governance.Requests.DeputyTestSupport;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Issue #1126: full-state reconciliation, incremental changes, reads and deprovisioning of external deputy relations.</summary>
[Trait("Category", "Security")]
public sealed class DeputyRelationServiceTests
{
    private readonly InMemoryDeputyRelationRepository _relations = new();
    private readonly FakeTimeProvider _time = new(Now);

    private DeputyRelationService Service(Autheris.Domain.Interfaces.IIdentityStoreRepository? directory = null)
    {
        var store = directory ?? Directory();
        return new DeputyRelationService(
            _relations,
            new ScimDeputyIdentityResolver(store, Realms(Realm), store),
            new GovernanceCapabilities(domainRepository: DomainRepository()),
            new DeputyAuthority(_relations, store, _time),
            DomainRepository(),
            timeProvider: _time);
    }

    private static IIdentityRealmRegistry Realms(params string[] ids) =>
        NullFake.Create<IIdentityRealmRegistry>((m, args) => m.Name switch
        {
            nameof(IIdentityRealmRegistry.GetAllRealms) => (IReadOnlyList<IdentityRealm>)ids.Select(id => new IdentityRealm(id, IdentityRealmKind.Customer, TenantBindingSpecification.Fixed(TenantA))).ToList(),
            nameof(IIdentityRealmRegistry.TryGetRealm) => ids.Contains((string)args![0]!),
            _ => NullFake.UseDefault
        });

    private static SecurityPrincipalContext SyncIdentity(TenantId? tenant = null) => ContextFor("sp:hr-sync", tenant: tenant);

    private static DeputyRelationEntry Entry(string id, Person deputy, Person principal, int fromDays = -1, int toDays = 10) =>
        new(id, principal.Reference, deputy.Reference, Now.AddDays(fromDays), Now.AddDays(toDays));

    [Fact]
    public async Task Full_state_sync_creates_updates_and_ends_only_relations_of_its_source_and_tenant()
    {
        var other = Relation(Carol, Bob, Now.AddDays(-1), Now.AddDays(5), source: "k2", externalId: "x");
        var foreign = Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5), externalId: "1", tenant: TenantB);
        _relations.Rows.AddRange([other, foreign]);
        var service = Service();

        var first = await service.ReconcileAsync(SyncIdentity(), TenantA, holdsSyncRight: true, "hr",
            [Entry("1", Alice, Bob), Entry("2", Dave, Carol)], dryRun: false);
        first.Outcome.ShouldBe(DeputyRelationOutcome.Ok);
        first.Created.Count.ShouldBe(2);

        var second = await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Alice, Bob, toDays: 20)], dryRun: false);
        second.Updated.ShouldHaveSingleItem().After.ValidTo.ShouldBe(Now.AddDays(20));
        second.Ended.ShouldHaveSingleItem().ExternalId.ShouldBe("2");

        _relations.Rows.ShouldContain(other);
        _relations.Rows.ShouldContain(foreign);
        _relations.Rows.Count(r => r.Source == "hr" && r.TenantId == TenantA).ShouldBe(1);

        var again = await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Alice, Bob, toDays: 20)], dryRun: false);
        (again.Created.Count + again.Updated.Count + again.Ended.Count).ShouldBe(0, "reconciliation is idempotent");
        again.Unchanged.ShouldBe(1);
    }

    [Fact]
    public async Task Dry_run_returns_the_diff_and_changes_nothing()
    {
        var result = await Service().ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Alice, Bob)], dryRun: true);

        result.DryRun.ShouldBeTrue();
        result.Created.ShouldHaveSingleItem();
        _relations.Rows.ShouldBeEmpty();
        _relations.ApplyCalls.ShouldBe(0);
        _relations.Audit.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_change_is_audited_with_source_external_id_and_before_after()
    {
        var service = Service();
        await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Alice, Bob)], dryRun: false);
        await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Alice, Bob, toDays: 30)], dryRun: false);
        await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [], dryRun: false);

        _relations.Audit.Select(a => a.EventType).ShouldBe(
            [AuditEventTypes.DeputyRelationCreated, AuditEventTypes.DeputyRelationUpdated, AuditEventTypes.DeputyRelationEnded]);
        AuditPolicyTable.For(AuditEventTypes.DeputyRelationEnded).ShouldBe(AuditFailurePolicy.FailClosed);
        using var updated = JsonDocument.Parse(_relations.Audit[1].DetailsJson!);
        updated.RootElement.GetProperty("source").GetString().ShouldBe("hr");
        updated.RootElement.GetProperty("externalId").GetString().ShouldBe("1");
        updated.RootElement.GetProperty("before").GetProperty("validTo").GetDateTimeOffset().ShouldBe(Now.AddDays(10));
        updated.RootElement.GetProperty("after").GetProperty("validTo").GetDateTimeOffset().ShouldBe(Now.AddDays(30));
        _relations.Audit[2].TenantId.ShouldBe(TenantA);
    }

    [Fact]
    public async Task Unauthorised_callers_are_refused()
    {
        var service = Service();
        var entries = new[] { Entry("1", Alice, Bob) };

        (await service.ReconcileAsync(ContextFor("user:it", "TenantAdmin"), TenantA, false, "hr", entries, false)).Outcome.ShouldBe(DeputyRelationOutcome.Forbidden);
        (await service.ReconcileAsync(ContextFor("user:cluster", "ClusterAdmin"), TenantA, false, "hr", entries, false)).Outcome.ShouldBe(DeputyRelationOutcome.Forbidden);
        (await service.ReconcileAsync(ContextFor(Bob, groups: [OwnerGroupSid]), TenantA, false, "hr", entries, false)).Outcome
            .ShouldBe(DeputyRelationOutcome.Forbidden, "owners do not maintain deputies");
        (await service.ReconcileAsync(SyncIdentity(TenantB), TenantA, true, "hr", entries, false)).Outcome
            .ShouldBe(DeputyRelationOutcome.Forbidden, "the sync right is bound to the caller's tenant");
        (await service.ReconcileAsync(ContextFor("user:dg", "GovernanceAdmin", groupStatus: GroupResolutionStatus.Degraded), TenantA, false, "hr", entries, false))
            .Outcome.ShouldBe(DeputyRelationOutcome.Forbidden);
        (await service.DeleteAsync(ContextFor("user:it", "TenantAdmin"), TenantA, false, "hr", "1")).Outcome.ShouldBe(DeputyRelationOutcome.Forbidden);
        _relations.Rows.ShouldBeEmpty();

        (await service.ReconcileAsync(ContextFor("user:dg", "GovernanceAdmin"), TenantA, false, "hr", entries, false)).Outcome.ShouldBe(DeputyRelationOutcome.Ok);
    }

    [Fact]
    public async Task Unresolvable_ambiguous_and_invalid_entries_are_rejected_per_entry()
    {
        var ghost = new Person("ghost", "S-1-5-21-10-20-30-9999");
        var result = await Service(Directory(inactive: new HashSet<string> { Dave.UserKey })).ReconcileAsync(SyncIdentity(), TenantA, true, "hr",
        [
            Entry("ok", Alice, Bob),
            Entry("ghost", ghost, Bob),
            Entry("inactive", Dave, Bob),
            Entry("self", Bob, Bob),
            Entry("long", Alice, Carol, fromDays: 0, toDays: 91),
            new DeputyRelationEntry("noend", Bob.Reference, Alice.Reference, Now, null),
            Entry("dup", Alice, Carol),
            Entry("dup", Carol, Bob)
        ], dryRun: false);

        result.Created.ShouldHaveSingleItem().ExternalId.ShouldBe("ok");
        result.Rejected.Select(r => (r.ExternalId, r.Code)).ShouldBe(
        [
            ("ghost", DeputyRelationErrorCodes.DeputyUnresolvable),
            ("inactive", DeputyRelationErrorCodes.DeputyUnresolvable),
            ("self", DeputyRelationErrorCodes.SelfRelation),
            ("long", DeputyRelationErrorCodes.WindowInvalid),
            ("noend", DeputyRelationErrorCodes.WindowInvalid),
            ("dup", DeputyRelationErrorCodes.DuplicateExternalId),
            ("dup", DeputyRelationErrorCodes.DuplicateExternalId)
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Ambiguous_bare_identifier_is_rejected()
    {
        var store = Directory();
        var service = new DeputyRelationService(
            _relations, new ScimDeputyIdentityResolver(store, Realms("corp", "partner"), store), new GovernanceCapabilities(), timeProvider: _time);

        var result = await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr",
            [new DeputyRelationEntry("1", Bob.ObjectSid, Alice.Reference, Now, Now.AddDays(5))], dryRun: false);

        result.Rejected.ShouldHaveSingleItem().Code.ShouldBe(DeputyRelationErrorCodes.PrincipalAmbiguous);
        _relations.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_entry_ends_its_existing_relation()
    {
        var service = Service();
        await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Dave, Bob)], dryRun: false);

        var result = await Service(Directory(inactive: new HashSet<string> { Dave.UserKey }))
            .ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Dave, Bob)], dryRun: false);

        result.Rejected.ShouldHaveSingleItem();
        result.Ended.ShouldHaveSingleItem();
        _relations.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_is_atomic_when_storage_or_audit_fails()
    {
        var service = Service();
        await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("1", Alice, Bob)], dryRun: false);
        var before = _relations.Rows.ToList();
        _relations.FailApplyWith = new AuditUnavailableException("audit down");

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", [Entry("2", Carol, Bob)], dryRun: false));

        _relations.Rows.ShouldBe(before);
    }

    [Fact]
    public async Task Incremental_upsert_and_delete_touch_one_relation()
    {
        var service = Service();
        (await service.UpsertAsync(SyncIdentity(), TenantA, true, "hr", Entry("1", Alice, Bob))).Created.ShouldHaveSingleItem();
        (await service.UpsertAsync(SyncIdentity(), TenantA, true, "hr", Entry("2", Carol, Bob))).Created.ShouldHaveSingleItem();
        (await service.UpsertAsync(SyncIdentity(), TenantA, true, "hr", Entry("1", Alice, Bob, toDays: 3))).Updated.ShouldHaveSingleItem();

        (await service.DeleteAsync(SyncIdentity(), TenantA, true, "hr", "1")).Ended.ShouldHaveSingleItem();
        (await service.DeleteAsync(SyncIdentity(), TenantA, true, "hr", "1")).Outcome.ShouldBe(DeputyRelationOutcome.NotFound);
        _relations.Rows.ShouldHaveSingleItem().ExternalId.ShouldBe("2");

        var rejected = await service.UpsertAsync(SyncIdentity(), TenantA, true, "hr", Entry("2", Bob, Bob));
        rejected.Outcome.ShouldBe(DeputyRelationOutcome.Invalid);
        rejected.ErrorCode.ShouldBe(DeputyRelationErrorCodes.SelfRelation);
        _relations.Rows.ShouldHaveSingleItem().DeputyRef.ShouldBe(Carol.Reference);
    }

    [Fact]
    public async Task Owner_reads_the_effective_deputies_of_its_container_only()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5)));
        _relations.Rows.Add(Relation(Carol, Dave, Now.AddDays(-1), Now.AddDays(5)));
        var service = Service();
        var container = new ContainerRef(ContainerKind.Domain, DomainId);

        var owner = await service.ListAsync(ContextFor(Carol, groups: [OwnerGroupSid]), TenantA, false, new DeputyRelationQuery(Container: container));
        owner.Relations!.ShouldHaveSingleItem().DeputyRef.ShouldBe(Alice.Reference);

        (await service.ListAsync(ContextFor(Dave), TenantA, false, new DeputyRelationQuery(Container: container))).Outcome.ShouldBe(DeputyRelationOutcome.Forbidden);
        (await service.ListAsync(ContextFor(Carol, groups: [OwnerGroupSid]), TenantA, false, new DeputyRelationQuery())).Outcome.ShouldBe(DeputyRelationOutcome.Forbidden);
        (await service.ListAsync(ContextFor("user:dg", "GovernanceAdmin"), TenantA, false, new DeputyRelationQuery())).Relations!.Count.ShouldBe(2);
        (await service.ListAsync(SyncIdentity(), TenantA, true, new DeputyRelationQuery(Deputy: Alice.ObjectSid))).Relations!.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Deprovisioning_of_the_deputy_or_the_principal_ends_the_relation()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5)));
        _relations.Rows.Add(Relation(Carol, Dave, Now.AddDays(-1), Now.AddDays(5)));
        _relations.Rows.Add(Relation(Dave, Carol, Now.AddDays(-1), Now.AddDays(5), tenant: TenantB));
        var service = Service();

        (await service.EndAllForSubjectAsync(TenantA, new Sid(Alice.Reference), "deprovisioned")).ShouldBe(1);
        (await service.EndAllForSubjectAsync(TenantA, new Sid(Realm + "|" + Dave.ObjectSid), "deprovisioned")).ShouldBe(1);
        (await service.EndAllForSubjectAsync(TenantA, new Sid(Dave.UserKey), "bare forms are ignored")).ShouldBe(0);

        _relations.Rows.ShouldHaveSingleItem().TenantId.ShouldBe(TenantB);
        _relations.Audit.ShouldAllBe(a => a.EventType == AuditEventTypes.DeputyRelationEnded && a.ActorSid.Value == "SYSTEM:DEPROVISIONING");
    }

    [Fact]
    public async Task Deprovisioned_deputy_loses_the_capacity_immediately()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5)));
        var eligibility = new Autheris.Application.Governance.Requests.ApproverEligibility(
            new GovernanceCapabilities(domainRepository: DomainRepository()), new DeputyAuthority(_relations, Directory(), _time));
        var set = new Autheris.Application.Governance.Requests.ApproverSet(
            TenantA, ConsentRequestKind.Data, new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid, true, false, false,
            RequesterRef: "user:x", GranteeRef: "user:x");
        (await eligibility.EvaluateAsync(ContextFor(Alice), set)).ShouldBe(ApproverCapacity.DataOwner);

        await Service().EndAllForSubjectAsync(TenantA, new Sid(Bob.Reference), "deprovisioned");

        (await eligibility.EvaluateAsync(ContextFor(Alice), set)).ShouldBeNull();
    }

    [Fact]
    public async Task Invalid_source_and_oversized_payloads_are_refused()
    {
        var service = Service();

        (await service.ReconcileAsync(SyncIdentity(), TenantA, true, "../hr", [], false)).ErrorCode.ShouldBe(DeputyRelationErrorCodes.SourceInvalid);
        var many = Enumerable.Range(0, DeputyRelationService.MaxEntriesPerSource + 1).Select(i => Entry(i.ToString(), Alice, Bob)).ToList();
        (await service.ReconcileAsync(SyncIdentity(), TenantA, true, "hr", many, false)).ErrorCode.ShouldBe(DeputyRelationErrorCodes.TooManyEntries);
    }
}
