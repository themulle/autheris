namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayPoliciesRoleConsolidationTests
{
    public static readonly string[] LegacyRoleNames =
    [
        "DataOwner",
        "DataSteward",
        "Auditor",
        "Analyst",
        "Operator",
        "SchemaAdmin",
        "SecurityAdmin",
        "GatewayAdmin",
        "PlatformAdmin",
        "PrivacyAdmin",
        "DataProtectionOfficer",
        "AiAgent",
        "McpCaller",
        "Developer",
    ];

    private static readonly string[] RemovedPolicyFieldNames =
    [
        "Approver",
        "PrivacyAdmin",
        "SchemaAdmin",
        "OperatorOrAbove",
        "DataStewardOrAdmin",
        "AuditorOrAbove",
        "GovernanceAuditRead",
        "ApproverRoles",
        "PrivacyAdminRoles",
        "SchemaAdminRoles",
        "OperatorOrAboveRoles",
        "DataStewardOrAdminRoles",
        "AuditorOrAboveRoles",
        "GovernanceAuditReadRoles",
    ];

    private static ServiceProvider BuildAuthorizationProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(
        string scheme = "Bearer",
        string? tenantId = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-TEST-USER"),
        };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    private static HttpContext HttpContextWithPrincipal(ClaimsPrincipal principal, string? resolvedTenant)
    {
        var context = new DefaultHttpContext
        {
            User = principal,
        };
        if (!string.IsNullOrWhiteSpace(resolvedTenant))
        {
            context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(resolvedTenant);
            context.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.FromPrincipal(
                principal,
                overrideTenant: new TenantId(resolvedTenant));
        }

        return context;
    }

    [Fact]
    public async Task ClusterAdmin_only_through_canonical_policy()
    {
        foreach (var removedField in RemovedPolicyFieldNames)
        {
            typeof(GatewayPolicies).GetField(removedField, BindingFlags.Public | BindingFlags.Static)
                .ShouldBeNull($"Legacy GatewayPolicies field '{removedField}' must be removed.");
        }

        var roleArrayFields = typeof(GatewayPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string[]));

        foreach (var field in roleArrayFields)
        {
            var values = (string[])field.GetValue(null)!;
            values.ShouldNotContain("ClusterAdmin", $"GatewayPolicies.{field.Name} must not contain 'ClusterAdmin' string literal.");
            foreach (var legacy in LegacyRoleNames)
            {
                values.ShouldNotContain(legacy, $"GatewayPolicies.{field.Name} must not contain legacy role '{legacy}'.");
            }
        }

        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        var canonicalClusterAdmin = Principal(scheme: "Bearer", roles: "ClusterAdmin");
        var forwardAuthClusterAdmin = Principal(scheme: "ForwardAuth", roles: "ClusterAdmin");
        var tenantPrefixedClusterAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:ClusterAdmin");

        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.AdminOnly)).Succeeded.ShouldBeTrue();

        // ClusterAdmin does NOT imply GovernanceAdmin, FilterAdmin, CallerAttribute*, or DataProduct*.
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.VirtualFilterWrite)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.VirtualFilterApprove)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.CallerAttributeApprove)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.ProductSync)).Succeeded.ShouldBeFalse();

        // Non-canonical ClusterAdmin (ForwardAuth or tenant-prefixed) is denied everywhere ClusterAdmin is gated.
        (await authz.AuthorizeAsync(forwardAuthClusterAdmin, null, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(forwardAuthClusterAdmin, null, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(tenantPrefixedClusterAdmin, null, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(tenantPrefixedClusterAdmin, null, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();
    }

    public static IEnumerable<object[]> LegacyRoleCases() =>
        LegacyRoleNames.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(LegacyRoleCases))]
    public async Task Legacy_role_strings_satisfy_nothing(string legacyRole)
    {
        GatewayRoleExtensions.TryParseRole(legacyRole, out _).ShouldBeFalse($"TryParseRole('{legacyRole}') must return false.");

        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        var principal = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: legacyRole);
        var httpContext = HttpContextWithPrincipal(principal, "tenant-a");

        string[] policiesToDeny =
        [
            GatewayPolicies.ClusterAdmin,
            GatewayPolicies.ItAdmin,
            GatewayPolicies.GovernanceAdmin,
            GatewayPolicies.AdminOnly,
            GatewayPolicies.AuditRead,
            GatewayPolicies.SchemaPublisher,
            GatewayPolicies.McpCaller,
            GatewayPolicies.VirtualFilterRead,
            GatewayPolicies.VirtualFilterWrite,
            GatewayPolicies.VirtualFilterApprove,
            GatewayPolicies.CallerAttributeRead,
            GatewayPolicies.CallerAttributeApprove,
            GatewayPolicies.CallerAttributeSimulate,
            GatewayPolicies.ProductOwnerRead,
            GatewayPolicies.ProductOwnerAdmin,
            GatewayPolicies.ProductSync,
            GatewayPolicies.SunsettingRead,
            GatewayPolicies.SunsettingEvaluate,
            GatewayPolicies.ComplianceCertificateRead,
        ];

        foreach (var policy in policiesToDeny)
        {
            (await authz.AuthorizeAsync(principal, httpContext, policy)).Succeeded
                .ShouldBeFalse($"Legacy role '{legacyRole}' must be denied by policy '{policy}'.");
        }
    }

    [Fact]
    public async Task Consolidated_policies_enforce_role_and_tenant_boundaries()
    {
        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        // tenant-a:GovernanceAdmin satisfies GovernanceAdmin/AdminOnly/AuditRead only for tenant-a.
        var scopedGovAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:GovernanceAdmin");
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.AdminOnly)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.AuditRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-b"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-b"), GatewayPolicies.AuditRead)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();

        // TenantAdmin satisfies ItAdmin & AdminOnly & AuditRead (via SecurityAuditor implication), not GovernanceAdmin.
        var scopedTenantAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:TenantAdmin");
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-a"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-a"), GatewayPolicies.AdminOnly)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-a"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-b"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();

        // SecurityAuditor satisfies AuditRead, not GovernanceAdmin or ItAdmin.
        var auditor = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "SecurityAuditor");
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.AuditRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.AdminOnly)).Succeeded.ShouldBeFalse();
    }
}
