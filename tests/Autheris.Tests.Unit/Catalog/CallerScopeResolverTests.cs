namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>T-W0-4 (class matrix), T-W0-12, T-W0-13: the one canonical caller-class resolver and the tenancy option.</summary>
[Trait("Category", "Security")]
public sealed class CallerScopeResolverTests
{
    [Fact]
    public void Resolve_NoPrincipalOrUnauthenticatedOrNoSid_IsNone()
    {
        CallerScopeResolver.Resolve(null, TenancyShape.Multi.Owner()).ShouldBe(CallerScope.None);
        CallerScopeResolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()), TenancyShape.Single.Owner()).ShouldBe(CallerScope.None);
        CallerScopeResolver.Resolve(Wp0.User("u", "tenant-a", ["ClusterAdmin"], authType: ""), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.None);
        var noSid = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")], "Bearer"));
        CallerScopeResolver.Resolve(noSid, TenancyShape.Multi.Owner()).ShouldBe(CallerScope.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DT_SC_02_Resolve_ClusterAdmin_With_Invalid_Tenant_Claim_IsNone_ClusterAdmin_Without_Claim_IsGlobal()
    {
        CallerScopeResolver.Resolve(Wp0.User("root", "bad tenant!!", ["ClusterAdmin"]), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.None);
        CallerScopeResolver.Resolve(Wp0.User("root", "bad tenant!!", ["ClusterAdmin"]), TenancyShape.Single.Owner()).ShouldBe(CallerScope.None);
        CallerScopeResolver.Resolve(Wp0.User("root", null, ["ClusterAdmin"]), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.Global);
    }

    [Theory]
    [InlineData(TenancyShape.Multi)]
    [InlineData(TenancyShape.Single)]
    public void Resolve_CanonicalClusterAdmin_IsGlobalInBothModes(TenancyShape mode)
        => CallerScopeResolver.Resolve(Wp0.User("root", null, ["ClusterAdmin"]), mode.Owner()).ShouldBe(CallerScope.Global);

    [Theory]
    [InlineData("tenant:ClusterAdmin")]
    [InlineData("GatewayAdmin")]
    [InlineData("PlatformAdmin")]
    public void Resolve_TenantScopedAdminAliases_AreNotGlobal(string role)
        => CallerScopeResolver.Resolve(Wp0.User("u", "tenant-a", [role]), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.Subject);

    [Fact]
    public void Resolve_ForwardAuthIdentityClaimingClusterAdmin_IsNotGlobal()
        => CallerScopeResolver.Resolve(Wp0.User("u", "tenant-a", ["ClusterAdmin"], authType: "ForwardAuth"), TenancyShape.Multi.Owner())
            .ShouldBe(CallerScope.Subject);

    [Fact]
    public void Resolve_Multi_GovernanceAdminWithTenantClaim_IsTenant()
        => CallerScopeResolver.Resolve(Wp0.User("a", "tenant-a", ["GovernanceAdmin"]), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.Tenant);

    [Fact]
    public void Resolve_Multi_PrincipalWithoutTenantClaim_IsNoneEvenWithGovernanceAdmin()
    {
        CallerScopeResolver.Resolve(Wp0.User("a", null, ["GovernanceAdmin"]), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.None);
        CallerScopeResolver.Resolve(Wp0.User("a", null), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.None);
    }

    [Fact]
    public void Resolve_Multi_PlainUserWithTenantClaim_IsSubject()
        => CallerScopeResolver.Resolve(Wp0.User("a", "tenant-a"), TenancyShape.Multi.Owner()).ShouldBe(CallerScope.Subject);

    [Fact]
    public void Resolve_Single_GovernanceAdminWithoutClaim_IsCatalogOwner()
        => CallerScopeResolver.Resolve(Wp0.User("a", null, ["GovernanceAdmin"]), TenancyShape.Single.Owner()).ShouldBe(CallerScope.CatalogOwner);

    [Fact]
    public void Resolve_Single_GovernanceAdminWithRealTenantClaim_IsTenantNotCatalogOwner()
        => CallerScopeResolver.Resolve(Wp0.User("a", "tenant-a", ["GovernanceAdmin"]), TenancyShape.Single.Owner()).ShouldBe(CallerScope.Tenant);

    [Fact]
    public void Resolve_Single_PlainUser_IsSubject()
        => CallerScopeResolver.Resolve(Wp0.User("a", null), TenancyShape.Single.Owner()).ShouldBe(CallerScope.Subject);

    [Theory]
    [InlineData(TenancyShape.Multi)]
    [InlineData(TenancyShape.Single)]
    public void Resolve_InvalidTenantClaim_IsNoneNeverWider(TenancyShape mode)
        => CallerScopeResolver.Resolve(Wp0.User("a", "bad tenant!!", ["GovernanceAdmin"]), mode.Owner()).ShouldBe(CallerScope.None);

    [Fact]
    public void Resolve_IgnoresRoleStringsOutsideThePrincipal()
    {
        // A RequestContext.Roles list or CallerSecurityContext.IsClusterAdmin never reaches the resolver: only the principal counts.
        var plain = Wp0.User("a", "tenant-a");
        var context = new Autheris.Domain.Model.RequestContext(Wp0.TenantA, new Autheris.Domain.Common.Sid("a"), ["ClusterAdmin", "GovernanceAdmin"], null, plain);
        CallerScopeResolver.Resolve(context.User, TenancyShape.Multi.Owner()).ShouldBe(CallerScope.Subject);
    }

    [Fact]
    public void ClaimlessAdminDiagnostic_IsThrottledPerPrincipalAndWindow()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var admin = Wp0.User("diag-admin-" + System.Guid.NewGuid().ToString("N"), null, ["GovernanceAdmin"]);
        var plain = Wp0.User("diag-plain-" + System.Guid.NewGuid().ToString("N"), null);

        long now = 1_000_000_000;
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(logger, admin, CallerScope.None, now);
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(logger, admin, CallerScope.None, now + 10);
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(logger, plain, CallerScope.None, now);
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(logger, admin, CallerScope.Tenant, now);
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log").ShouldBe(1);

        // After the window the same principal is reported again.
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(logger, admin, CallerScope.None, now + System.TimeSpan.FromMinutes(11).Ticks);
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log").ShouldBe(2);
    }
}
