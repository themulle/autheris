using System.Security.Claims;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class PermissionSeparationOfDutiesTests
{
    private static readonly TenantId Tenant = new("tenant-sod");

    private static ClaimsPrincipal BoundPrincipal(string realmId, string userSid, string oid, string? sub = null)
    {
        var claims = new List<Claim>
        {
            IdentityClaimIssuers.CreatePrimarySidClaim(userSid),
            new("autheris:realm", realmId),
            new("oid", oid)
        };
        if (!string.IsNullOrWhiteSpace(sub))
        {
            claims.Add(new Claim("sub", sub));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    public async Task Realm_bound_identifiers_compared()
    {
        const string sharedOid = "11223344-5566-7788-99aa-bbccddeeff00";
        const string sharedSub = "shared-subject-77";
        const string userKey = "scim-user-1";

        var realmA = new IdentityRealm("realm-a", IdentityRealmKind.GenericOidc, TenantBindingSpecification.Fixed(Tenant));
        var realmB = new IdentityRealm("realm-b", IdentityRealmKind.GenericOidc, TenantBindingSpecification.Fixed(Tenant));
        var registry = new FakeIdentityRealmRegistry([realmA, realmB], realmA);

        var queriedRealms = new List<string>();
        var queriedUserKeys = new List<string>();
        var groupStore = NullFake.Create<IIdentityStoreRepository>((method, args) =>
        {
            if (method.Name == nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync))
            {
                var rId = (string)args![1]!;
                var uKey = (string)args[2]!;
                queriedRealms.Add(rId);
                queriedUserKeys.Add(uKey);
                var sids = rId == "realm-a" && string.Equals(uKey, userKey, StringComparison.Ordinal)
                    ? new HashSet<string>(["realm:realm-a:grp-owners"], StringComparer.OrdinalIgnoreCase)
                    : rId == "realm-b" && string.Equals(uKey, userKey, StringComparison.Ordinal)
                        ? new HashSet<string>(["realm:realm-b:grp-owners"], StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return Task.FromResult(new EffectiveScimGroupsResult(sids, MaxDepthExceeded: false, HasNonCanonicalSid: false));
            }

            if (method.Name == nameof(IScimGroupStore.GetGroupMembershipsForUserAsync))
            {
                var rId = (string)args![1]!;
                var uKey = (string)args[2]!;
                return Task.FromResult<IReadOnlyList<string>>(
                    string.Equals(uKey, userKey, StringComparison.Ordinal)
                        ? [rId == "realm-a" ? "realm:realm-a:grp-owners" : "realm:realm-b:grp-owners"]
                        : []);
            }

            return NullFake.UseDefault;
        });

        var sut = new SeparationOfDutiesService(groupStore, registry);

        // 1. AddIdentifierForms must NOT strip realm qualifiers, while retaining type prefixes like user:/sp:/group:.
        SeparationOfDutiesService.IdentifiersOverlap("realm:realm-a:grp-owners", "realm:realm-b:grp-owners").ShouldBeFalse();
        SeparationOfDutiesService.IdentifiersOverlap("realm:realm-a:grp-owners", "grp-owners").ShouldBeFalse();
        SeparationOfDutiesService.IdentifiersOverlap("realm-a|oid-1", "realm-b|oid-1").ShouldBeFalse();
        SeparationOfDutiesService.IdentifiersOverlap("user:bob@corp.local", "bob@corp.local").ShouldBeTrue();
        SeparationOfDutiesService.IdentifiersOverlap("sp:worker-9", "worker-9").ShouldBeTrue();
        SeparationOfDutiesService.IdentifiersOverlap("group:S-1-5-21-1-2-3-512", "S-1-5-21-1-2-3-512").ShouldBeTrue();

        // 2. Identical oid/sub in different realms are distinct actors in SoD approval checks.
        var requesterPrincipal = BoundPrincipal("realm-a", "realm-a|" + sharedOid, sharedOid, sharedSub);
        var requesterIds = SeparationOfDutiesService.CollectSubjectIdentifiers(requesterPrincipal, "realm-a|" + sharedOid);
        var request = new ConsentRequest
        {
            TableIdentifier = new TableIdentifier("fin", "dbo", "ledger"),
            RequesterSid = new Sid("realm-a|" + sharedOid),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "realm-a|" + sharedOid,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Tenant,
            RequesterIdentifiers = [.. requesterIds]
        };

        var approverOtherRealm = BoundPrincipal("realm-b", "realm-b|" + sharedOid, sharedOid, sharedSub);
        var otherRealmDecision = await sut.CheckConsentApprovalAsync(
            Tenant, request, approverOtherRealm, new Sid("realm-b|" + sharedOid), ct: CancellationToken.None);
        otherRealmDecision.Allowed.ShouldBeTrue();

        var sameRealmDecision = await sut.CheckConsentApprovalAsync(
            Tenant, request, requesterPrincipal, new Sid("realm-a|" + sharedOid), ct: CancellationToken.None);
        sameRealmDecision.Allowed.ShouldBeFalse();
        sameRealmDecision.Kind.ShouldBe(SodViolationKind.SelfApproval);

        // 3. CheckGroupMembershipAsync must resolve the caller's bound realm ("realm-b"), not fall back to default ("realm-a").
        var callerInB = BoundPrincipal("realm-b", $"realm-b|{userKey}", userKey);
        var foreignGroupDecision = await sut.CheckGroupMembershipAsync(
            Tenant, callerInB, new Sid($"realm-b|{userKey}"), null, [userKey], "realm:realm-a:grp-owners", CancellationToken.None);
        foreignGroupDecision.Allowed.ShouldBeTrue();
        queriedRealms.ShouldContain("realm-b");
        queriedRealms.ShouldNotContain("realm-a");

        var sameRealmGroupDecision = await sut.CheckGroupMembershipAsync(
            Tenant, callerInB, new Sid($"realm-b|{userKey}"), null, [userKey], "realm:realm-b:grp-owners", CancellationToken.None);
        sameRealmGroupDecision.Allowed.ShouldBeFalse();
        sameRealmGroupDecision.Kind.ShouldBe(SodViolationKind.SelfApproval);

        // 4. Null principal with callerContext falls back to callerContext.Principal realm and strips matching realm prefix when querying store.
        queriedRealms.Clear();
        queriedUserKeys.Clear();
        var ctxInB = new SecurityPrincipalContext
        {
            UserSid = new Sid($"realm-b|{userKey}"),
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = callerInB
        };

        var ctxSameRealmDeny = await sut.CheckGroupMembershipAsync(
            Tenant, principal: null, new Sid($"realm-b|{userKey}"), ctxInB, lookupKeys: null, "realm:realm-b:grp-owners", CancellationToken.None);
        ctxSameRealmDeny.Allowed.ShouldBeFalse();
        ctxSameRealmDeny.Kind.ShouldBe(SodViolationKind.SelfApproval);
        queriedRealms.ShouldNotBeEmpty();
        queriedRealms.ShouldAllBe(r => r == "realm-b");
        queriedUserKeys.ShouldContain(userKey);

        var ctxOtherRealmAllow = await sut.CheckGroupMembershipAsync(
            Tenant, principal: null, new Sid($"realm-b|{userKey}"), ctxInB, lookupKeys: null, "realm:realm-a:grp-owners", CancellationToken.None);
        ctxOtherRealmAllow.Allowed.ShouldBeTrue();
    }
}
