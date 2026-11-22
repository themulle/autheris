using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class ApproverResolverTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly Guid DomainId = Guid.NewGuid();
    private static readonly Sid OwnerGroupSid = new("group:S-1-5-21-OWNER-GRP");

    private static GovernanceDomain MakeDomain(Guid id, TenantId tenant, string name, Sid? ownerGroupSid) =>
        new(id, tenant, name, null, null, ownerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static EffectiveProtection MakeProtection(ContainerRef container, Sid? ownerGroupSid, ProtectionClass tableClass = ProtectionClass.Confidential) =>
        new(container, ownerGroupSid, tableClass, tableClass, null, new Dictionary<string, ColumnProtection>(), CatalogVisibilityMode.Open);

    [Fact]
    public async Task Catalog_returns_all_three()
    {
        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, _) =>
            m.Name == nameof(IGovernanceDomainRepository.GetAsync)
                ? Task.FromResult<GovernanceDomain?>(MakeDomain(DomainId, TenantA, "Sales", OwnerGroupSid))
                : NullFake.UseDefault);

        var resolver = new ApproverResolver(domainRepository: domainRepo);
        var set = await resolver.ResolveAsync(
            TenantA,
            ConsentRequestKind.Catalog,
            PermissionTarget.ForContainer(ContainerKind.Domain, DomainId),
            new Sid("user:alice"),
            "user:alice");

        set.OwnerGroupAssigned.ShouldBeTrue();
        set.OwnerGroupSid.ShouldBe(OwnerGroupSid);
        set.GovernanceEligible.ShouldBeTrue();
        set.ItAdminEligible.ShouldBeTrue();
    }

    [Fact]
    public async Task Data_with_owner_returns_owners_only()
    {
        var protection = new StubProtectionProvider(() => MakeProtection(new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid));

        var resolver = new ApproverResolver(protectionSnapshotProvider: protection);
        var set = await resolver.ResolveAsync(
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(OrdersTable),
            new Sid("user:requester"),
            "user:grantee");

        set.OwnerGroupAssigned.ShouldBeTrue();
        set.OwnerGroupSid.ShouldBe(OwnerGroupSid);
        set.GovernanceEligible.ShouldBeFalse();
        set.ItAdminEligible.ShouldBeFalse();
        set.A7GovernanceFallback.ShouldBeFalse();
    }

    [Fact]
    public async Task Data_without_owner_returns_dg_and_it()
    {
        var protection = new StubProtectionProvider(() => MakeProtection(new ContainerRef(ContainerKind.Domain, DomainId), null));

        var resolver = new ApproverResolver(protectionSnapshotProvider: protection);
        var set = await resolver.ResolveAsync(
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(OrdersTable),
            new Sid("user:requester"),
            "user:grantee");

        set.OwnerGroupAssigned.ShouldBeFalse();
        set.GovernanceEligible.ShouldBeTrue();
        set.ItAdminEligible.ShouldBeTrue();
    }

    [Fact]
    public async Task A7_governance_eligible_when_requester_is_owner_member()
    {
        var protection = new StubProtectionProvider(() => MakeProtection(new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid));

        var groupStore = NullFake.Create<IScimGroupStore>((m, _) =>
            m.Name == nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync)
                ? Task.FromResult(new EffectiveScimGroupsResult(
                    new HashSet<string>([OwnerGroupSid.Value], StringComparer.OrdinalIgnoreCase),
                    MaxDepthExceeded: false,
                    HasNonCanonicalSid: false))
                : NullFake.UseDefault);

        var resolver = new ApproverResolver(protectionSnapshotProvider: protection, groupStore: groupStore);
        var set = await resolver.ResolveAsync(
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(OrdersTable),
            new Sid("owner-alice"),
            "owner-alice");

        set.OwnerGroupAssigned.ShouldBeTrue();
        set.A7GovernanceFallback.ShouldBeTrue();
        set.GovernanceEligible.ShouldBeTrue();
        set.ItAdminEligible.ShouldBeFalse();
    }

    private sealed class StubProtectionProvider(Func<EffectiveProtection> get) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(get());
    }
}
