namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayRoleImplicationMatrixTests
{
    private static readonly GatewayRole[] CanonicalRoles =
    [
        GatewayRole.ClusterAdmin,
        GatewayRole.TenantAdmin,
        GatewayRole.GovernanceAdmin,
        GatewayRole.SecurityAuditor,
        GatewayRole.SchemaPublisher,
        GatewayRole.Consumer,
        GatewayRole.FilterAdmin,
        GatewayRole.FilterSync,
        GatewayRole.CallerAttributeApprover,
        GatewayRole.CallerAttributeInspector,
        GatewayRole.DataProductAdmin,
        GatewayRole.DataProductSync,
        GatewayRole.CatalogArtifactUploader,
    ];

    private static bool ExpectedImplies(GatewayRole granted, GatewayRole required)
    {
        if (granted == required)
        {
            return true;
        }

        if (required.IsTenantScopedOnly())
        {
            return false;
        }

        return granted switch
        {
            GatewayRole.ClusterAdmin => required is GatewayRole.TenantAdmin
                or GatewayRole.SecurityAuditor
                or GatewayRole.SchemaPublisher
                or GatewayRole.Consumer
                or GatewayRole.CatalogArtifactUploader,
            GatewayRole.TenantAdmin => required is GatewayRole.SecurityAuditor
                or GatewayRole.SchemaPublisher
                or GatewayRole.Consumer,
            GatewayRole.GovernanceAdmin => required is GatewayRole.SecurityAuditor
                or GatewayRole.SchemaPublisher
                or GatewayRole.Consumer,
            GatewayRole.SchemaPublisher or GatewayRole.SecurityAuditor => required is GatewayRole.Consumer,
            _ => false,
        };
    }

    public static IEnumerable<object[]> AllRolePairs()
    {
        var values = Enum.GetValues<GatewayRole>();
        foreach (var granted in values)
        {
            foreach (var required in values)
            {
                yield return [granted, required, ExpectedImplies(granted, required)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllRolePairs))]
    public void Matrix_is_exact(GatewayRole granted, GatewayRole required, bool expected)
    {
        granted.Implies(required).ShouldBe(expected, $"{granted}.Implies({required}) expected {expected}");
    }

    [Fact]
    public void ClusterAdmin_does_not_imply_GovernanceAdmin_or_CallerAttribute()
    {
        Enum.GetNames<GatewayRole>().OrderBy(x => x, StringComparer.Ordinal).ShouldBe(
            CanonicalRoles.Select(r => r.ToString()).OrderBy(x => x, StringComparer.Ordinal));

        GatewayRole[] nonImpliedByClusterAdmin =
        [
            GatewayRole.GovernanceAdmin,
            GatewayRole.CallerAttributeApprover,
            GatewayRole.CallerAttributeInspector,
            GatewayRole.FilterAdmin,
            GatewayRole.FilterSync,
            GatewayRole.DataProductAdmin,
            GatewayRole.DataProductSync,
        ];

        foreach (var role in nonImpliedByClusterAdmin)
        {
            GatewayRole.ClusterAdmin.Implies(role).ShouldBeFalse($"ClusterAdmin must not imply {role}");
        }

        GatewayRole[] impliedByClusterAdmin =
        [
            GatewayRole.ClusterAdmin,
            GatewayRole.TenantAdmin,
            GatewayRole.SecurityAuditor,
            GatewayRole.SchemaPublisher,
            GatewayRole.Consumer,
            GatewayRole.CatalogArtifactUploader,
        ];

        foreach (var role in impliedByClusterAdmin)
        {
            GatewayRole.ClusterAdmin.Implies(role).ShouldBeTrue($"ClusterAdmin must imply {role}");
        }

        var clusterAdminContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-CLUSTER-ADMIN"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(["ClusterAdmin"], StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "Bearer",
            ResolvedTenant = new TenantId("tenant-a"),
        };

        foreach (var role in nonImpliedByClusterAdmin)
        {
            clusterAdminContext.HasRole(role).ShouldBeFalse($"SecurityPrincipalContext for ClusterAdmin must not satisfy {role}");
        }

        foreach (var role in impliedByClusterAdmin)
        {
            clusterAdminContext.HasRole(role).ShouldBeTrue($"SecurityPrincipalContext for ClusterAdmin must satisfy {role}");
        }
    }
}
