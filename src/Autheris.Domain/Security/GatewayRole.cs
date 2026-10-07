namespace Autheris.Domain.Security;

using System;
using System.Collections.Generic;

/// <summary>
/// K-K10: Strongly-typed canonical roles for the Gateway RBAC engine.
/// Standardizes disparate role strings into an enterprise hierarchy.
/// </summary>
public enum GatewayRole
{
    // Global Platform & Administration
    ClusterAdmin = 1,
    TenantAdmin = 2,
    GovernanceAdmin = 3,
    SecurityAuditor = 4,

    // Data Domain & Stewardship (Can be tenant-scoped)
    DataOwner = 10,
    DataSteward = 11,
    SchemaPublisher = 12,
    Consumer = 20
}

public static class GatewayRoleExtensions
{
    private static readonly Dictionary<string, GatewayRole> NameToRole = new(StringComparer.Ordinal)
    {
        ["ClusterAdmin"] = GatewayRole.ClusterAdmin,
        ["TenantAdmin"] = GatewayRole.TenantAdmin,
        ["GovernanceAdmin"] = GatewayRole.GovernanceAdmin,
        ["SecurityAuditor"] = GatewayRole.SecurityAuditor,
        ["Auditor"] = GatewayRole.SecurityAuditor,
        ["DataOwner"] = GatewayRole.DataOwner,
        ["DataSteward"] = GatewayRole.DataSteward,
        ["SchemaPublisher"] = GatewayRole.SchemaPublisher,
        ["Consumer"] = GatewayRole.Consumer,
        ["Analyst"] = GatewayRole.Consumer
    };

    public static bool TryParseRole(string? roleName, out GatewayRole role)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            role = default;
            return false;
        }

        // Support tenant-prefixed roles: "tenant-id:DataOwner"
        var colonIdx = roleName.LastIndexOf(':');
        var lookupName = colonIdx >= 0 ? roleName[(colonIdx + 1)..] : roleName;

        return NameToRole.TryGetValue(lookupName.Trim(), out role);
    }

    /// <summary>
    /// Checks role hierarchy: returns true if the granted role satisfies the required role.
    /// </summary>
    public static bool Implies(this GatewayRole granted, GatewayRole required)
    {
        if (granted == required)
        {
            return true;
        }

        // ClusterAdmin implies all roles
        if (granted == GatewayRole.ClusterAdmin)
        {
            return true;
        }

        // SEC M-1: TenantAdmin implies stewardship, publishing, auditing, consumer within tenant scope.
        // It NEVER implies GovernanceAdmin (global governance) or ClusterAdmin.
        if (granted == GatewayRole.TenantAdmin)
        {
            return required is GatewayRole.DataOwner or GatewayRole.DataSteward or GatewayRole.SchemaPublisher or GatewayRole.SecurityAuditor or GatewayRole.Consumer;
        }

        // GovernanceAdmin implies stewardship, publishing, auditing, consumer
        if (granted == GatewayRole.GovernanceAdmin)
        {
            return required is GatewayRole.DataOwner or GatewayRole.DataSteward or GatewayRole.SchemaPublisher or GatewayRole.SecurityAuditor or GatewayRole.Consumer;
        }

        // DataOwner implies DataSteward and Consumer
        if (granted == GatewayRole.DataOwner)
        {
            return required is GatewayRole.DataSteward or GatewayRole.Consumer;
        }

        // DataSteward implies Consumer
        if (granted == GatewayRole.DataSteward)
        {
            return required is GatewayRole.Consumer;
        }

        // SchemaPublisher implies Consumer
        if (granted == GatewayRole.SchemaPublisher)
        {
            return required is GatewayRole.Consumer;
        }

        return false;
    }
}
