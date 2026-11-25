using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed partial class GrantRevokeAuthorityTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Guid DomainId = Guid.NewGuid();
    private static readonly Sid OwnerGroupSid = new("group:S-1-5-21-OWNER-1");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");

    private static SecurityPrincipalContext ContextFor(
        string sid,
        string? role = null,
        Sid[]? groups = null,
        TenantId? tenant = null,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete)
    {
        var resolved = tenant ?? TenantA;
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", resolved.Value) };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = resolved,
            GroupSids = groups?.ToHashSet() ?? new HashSet<Sid>(),
            GroupResolutionStatus = groupStatus,
            TenantRoles = role is null ? new HashSet<string>() : new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
    }

    private static GrantRevokeAuthority Authority(Sid? ownerGroup)
    {
        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, _) =>
            m.Name == nameof(IGovernanceDomainRepository.GetAsync)
                ? Task.FromResult<GovernanceDomain?>(new GovernanceDomain(DomainId, TenantA, "Sales", null, null, ownerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : NullFake.UseDefault);
        var protection = new StubProtectionProvider(() => new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, DomainId),
            ownerGroup,
            ProtectionClass.Confidential,
            ProtectionClass.Confidential,
            null,
            new Dictionary<string, ColumnProtection>(),
            CatalogVisibilityMode.Open));
        var caps = new GovernanceCapabilities(domainRepository: domainRepo);
        return new GrantRevokeAuthority(
            new ApproverResolver(domainRepo, protectionSnapshotProvider: protection),
            new ApproverEligibility(caps),
            caps);
    }

    private static Consent GrantTo(string granteeSid, GranteeType type = GranteeType.User) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        TableIdentifier = OrdersTable,
        GranteeType = type,
        GranteeSid = new Sid(granteeSid),
        ValidFrom = DateTimeOffset.UnixEpoch,
        ValidTo = DateTimeOffset.UnixEpoch.AddYears(80)
    };

    [Fact]
    public async Task Grantee_can_revoke_own_grant()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(ContextFor("user:alice"), GrantTo("user:alice")))
            .ShouldBe(GrantRevokeCapacity.Grantee);
    }

    [Fact]
    public async Task Owner_group_member_can_revoke()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(ContextFor("user:owner", groups: [OwnerGroupSid]), GrantTo("user:alice")))
            .ShouldBe(GrantRevokeCapacity.ContainerOwner);
    }

    [Fact]
    public async Task Data_governance_can_revoke()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(ContextFor("user:dg", "GovernanceAdmin"), GrantTo("user:alice")))
            .ShouldBe(GrantRevokeCapacity.Governance);
    }

    [Fact]
    public async Task It_cannot_revoke_a_grant_of_a_container_with_an_owner_group()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(ContextFor("user:it", "TenantAdmin"), GrantTo("user:alice"))).ShouldBeNull();
    }

    [Fact]
    public async Task It_can_revoke_a_grant_it_may_decide_when_no_owner_group_is_assigned()
    {
        var authority = Authority(ownerGroup: null);

        (await authority.EvaluateAsync(ContextFor("user:it", "TenantAdmin"), GrantTo("user:alice")))
            .ShouldBe(GrantRevokeCapacity.ItAdmin);
    }

    [Fact]
    public async Task Unrelated_user_and_grantee_group_member_cannot_revoke()
    {
        var authority = Authority(OwnerGroupSid);
        var granteeGroup = new Sid("group:S-1-5-21-GRANTEES");

        (await authority.EvaluateAsync(ContextFor("user:mallory"), GrantTo("user:alice"))).ShouldBeNull();
        (await authority.EvaluateAsync(ContextFor("user:bob", groups: [granteeGroup]), GrantTo(granteeGroup.Value, GranteeType.Group))).ShouldBeNull();
    }

    [Fact]
    public async Task Degraded_group_resolution_denies_the_owner_member()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(
            ContextFor("user:owner", groups: [OwnerGroupSid], groupStatus: GroupResolutionStatus.Degraded),
            GrantTo("user:alice"))).ShouldBeNull();
    }

    [Fact]
    public async Task Caller_of_another_tenant_cannot_revoke_even_as_grantee_or_owner()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(ContextFor("user:alice", tenant: TenantB), GrantTo("user:alice"))).ShouldBeNull();
        (await authority.EvaluateAsync(ContextFor("user:owner", groups: [OwnerGroupSid], tenant: TenantB), GrantTo("user:alice"))).ShouldBeNull();
    }

    private sealed class StubProtectionProvider(Func<EffectiveProtection> get) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(get());
    }
}
