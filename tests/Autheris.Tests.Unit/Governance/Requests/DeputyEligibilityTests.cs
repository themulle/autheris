using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Governance.Requests.DeputyTestSupport;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Issue #1126: a deputy (A) acting for a principal (B) as an eligible decider through the single eligibility seam.</summary>
[Trait("Category", "Security")]
public sealed class DeputyEligibilityTests
{
    private readonly InMemoryDeputyRelationRepository _relations = new();
    private readonly FakeTimeProvider _time = new(Now);

    private ApproverEligibility Eligibility(Autheris.Domain.Interfaces.IIdentityStoreRepository? directory = null) =>
        new(new GovernanceCapabilities(domainRepository: DomainRepository()), new DeputyAuthority(_relations, directory ?? Directory(), _time));

    private static ApproverSet DataSet(
        ConsentRequestKind kind = ConsentRequestKind.Data,
        string requester = "user:requester",
        string grantee = "user:requester",
        bool governanceEligible = false) =>
        new(TenantA, kind, new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid, OwnerGroupAssigned: true,
            GovernanceEligible: governanceEligible, ItAdminEligible: false, RequesterRef: requester, GranteeRef: grantee);

    [Fact]
    public async Task Deputy_decides_for_an_owner_member_only_inside_the_window()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(1), Now.AddDays(10)));
        var eligibility = Eligibility();
        var alice = ContextFor(Alice);

        (await eligibility.EvaluateAsync(alice, DataSet())).ShouldBeNull("before the window");

        _time.SetUtcNow(Now.AddDays(1));
        var inside = await eligibility.EvaluateDetailedAsync(alice, DataSet());
        inside.ShouldNotBeNull();
        inside.Value.Capacity.ShouldBe(ApproverCapacity.DataOwner);
        inside.Value.OnBehalfOf.ShouldBe(Bob.Reference, "four-eyes (#1121) counts A-for-B and B as one authority");
        (await eligibility.EvaluateAsync(alice, DataSet(ConsentRequestKind.Unmask))).ShouldBe(ApproverCapacity.DataOwner);

        _time.SetUtcNow(Now.AddDays(10));
        (await eligibility.EvaluateAsync(alice, DataSet())).ShouldBeNull("the end of the window is exclusive");
    }

    [Fact]
    public async Task Deputy_has_nothing_where_the_principal_is_not_an_owner_member()
    {
        _relations.Rows.Add(Relation(Alice, Dave, Now.AddDays(-1), Now.AddDays(10)));

        (await Eligibility().EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
    }

    [Fact]
    public async Task Deputy_never_receives_data_governance_capacity()
    {
        // Dave holds Data Governance but is no owner member; a deputy for Dave gets nothing, even where DG would be eligible.
        _relations.Rows.Add(Relation(Alice, Dave, Now.AddDays(-1), Now.AddDays(10)));
        var eligibility = Eligibility();

        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(governanceEligible: true))).ShouldBeNull();
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(ConsentRequestKind.Catalog, governanceEligible: true))).ShouldBeNull();
    }

    [Fact]
    public async Task Deputy_capacity_is_limited_to_data_and_unmask_requests()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10)));
        var eligibility = Eligibility();

        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(ConsentRequestKind.Catalog))).ShouldBeNull();
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(ConsentRequestKind.ProfileAssignment))).ShouldBeNull();
    }

    [Fact]
    public async Task Relations_never_chain()
    {
        // Alice deputizes for Dave, Dave deputizes for Bob (owner member): Alice gets nothing through Dave.
        _relations.Rows.Add(Relation(Alice, Dave, Now.AddDays(-1), Now.AddDays(10)));
        _relations.Rows.Add(Relation(Dave, Bob, Now.AddDays(-1), Now.AddDays(10)));
        var eligibility = Eligibility();

        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
        (await eligibility.EvaluateAsync(ContextFor(Dave), DataSet())).ShouldBe(ApproverCapacity.DataOwner);
    }

    [Fact]
    public async Task Deputy_never_acts_for_the_principal_on_its_own_request_or_benefit()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10)));
        var eligibility = Eligibility();

        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(requester: Bob.ObjectSid, grantee: "user:someone"))).ShouldBeNull();
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(requester: "user:someone", grantee: Bob.Reference))).ShouldBeNull();
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet(requester: "user:someone", grantee: OwnerGroupSid.Value)))
            .ShouldBeNull("a grant to a group B belongs to benefits B");
    }

    [Fact]
    public async Task Removal_and_expiry_take_effect_immediately()
    {
        var relation = Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10));
        _relations.Rows.Add(relation);
        var eligibility = Eligibility();
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBe(ApproverCapacity.DataOwner);

        _relations.Rows.Remove(relation);
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull("no cache keeps a removed relation alive");

        _relations.Rows.Add(relation with { ValidTo = Now });
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
    }

    [Fact]
    public async Task Locked_deputy_or_principal_has_no_capacity()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10)));

        (await Eligibility(Directory(locked: new HashSet<string> { Alice.UserKey })).EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
        (await Eligibility(Directory(locked: new HashSet<string> { Bob.UserKey })).EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
    }

    [Fact]
    public async Task Degraded_unqualified_foreign_tenant_or_application_callers_never_act_as_deputy()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10)));
        var eligibility = Eligibility();

        (await eligibility.EvaluateAsync(ContextFor(Alice, groupStatus: GroupResolutionStatus.Degraded), DataSet())).ShouldBeNull();
        (await eligibility.EvaluateAsync(ContextFor(Alice, withRealm: false), DataSet())).ShouldBeNull("a bare identifier never matches");
        (await eligibility.EvaluateAsync(ContextFor(Alice, tenant: TenantB), DataSet())).ShouldBeNull();

        // The relation of another tenant never applies.
        _relations.Rows.Clear();
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10), tenant: TenantB));
        (await eligibility.EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
    }

    [Fact]
    public async Task Without_the_deputy_seam_nobody_acts_as_deputy()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10)));

        (await new ApproverEligibility(new GovernanceCapabilities(domainRepository: DomainRepository())).EvaluateAsync(ContextFor(Alice), DataSet())).ShouldBeNull();
    }

    [Fact]
    public async Task Owner_member_keeps_its_own_capacity_without_on_behalf_of()
    {
        var result = await Eligibility().EvaluateDetailedAsync(ContextFor(Bob, groups: [OwnerGroupSid]), DataSet());

        result.ShouldNotBeNull();
        result.Value.Capacity.ShouldBe(ApproverCapacity.DataOwner);
        result.Value.OnBehalfOf.ShouldBeNull();
    }

    [Fact]
    public async Task Deputy_revokes_with_the_deputy_capacity()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(10)));
        var domains = DomainRepository();
        var caps = new GovernanceCapabilities(domainRepository: domains);
        var authority = new GrantRevokeAuthority(
            new ApproverResolver(domains, protectionSnapshotProvider: new FixedProtection()),
            new ApproverEligibility(caps, new DeputyAuthority(_relations, Directory(), _time)),
            caps);
        var grant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:someone"),
            ValidFrom = DateTimeOffset.UnixEpoch,
            ValidTo = Now.AddYears(1)
        };

        (await authority.EvaluateAsync(ContextFor(Alice), grant)).ShouldBe(GrantRevokeCapacity.ContainerDeputy);
        var bobsGrant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = grant.TableIdentifier,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(Bob.Reference),
            ValidFrom = grant.ValidFrom,
            ValidTo = grant.ValidTo
        };
        (await authority.EvaluateAsync(ContextFor(Alice), bobsGrant))
            .ShouldBeNull("never revoke for B on B's own grant");
        (await authority.EvaluateAsync(ContextFor(Dave), grant)).ShouldBeNull();
    }

    private sealed class FixedProtection : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, System.Threading.CancellationToken ct = default) =>
            ValueTask.FromResult(new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, DomainId),
                OwnerGroupSid,
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.Open));
    }
}
