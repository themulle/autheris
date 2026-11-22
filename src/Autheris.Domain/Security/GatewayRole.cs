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

    // Schema Publishing & Consumption (Can be tenant-scoped)
    SchemaPublisher = 12,
    Consumer = 20,

    // Virtual filters: maintenance and repository sync are separate and never implied by ClusterAdmin, GovernanceAdmin, or TenantAdmin.
    FilterAdmin = 30,
    FilterSync = 31,

    // Caller attributes: approving data-granting changes and revealing attribute values are separate and never implied by ClusterAdmin, GovernanceAdmin, or TenantAdmin.
    CallerAttributeApprover = 32,
    CallerAttributeInspector = 33,

    // Discovery artifact ingestion: uploading or deleting pre-computed discovery artifacts for tables in the caller's tenant.
    CatalogArtifactUploader = 34,

    // Data products: creating products over any objects can expose data base owners would not release, and the Git sync identity
    // creates products from a repository. Both are tenant-scoped only and implied by NO role, ClusterAdmin included.
    DataProductAdmin = 35,
    DataProductSync = 36
}

public static class GatewayRoleExtensions
{
    private static readonly Dictionary<string, GatewayRole> NameToRole = new(StringComparer.Ordinal)
    {
        ["ClusterAdmin"] = GatewayRole.ClusterAdmin,
        ["TenantAdmin"] = GatewayRole.TenantAdmin,
        ["GovernanceAdmin"] = GatewayRole.GovernanceAdmin,
        ["SecurityAuditor"] = GatewayRole.SecurityAuditor,
        ["SchemaPublisher"] = GatewayRole.SchemaPublisher,
        ["Consumer"] = GatewayRole.Consumer,
        ["FilterAdmin"] = GatewayRole.FilterAdmin,
        ["FilterSync"] = GatewayRole.FilterSync,
        ["CallerAttributeApprover"] = GatewayRole.CallerAttributeApprover,
        ["CallerAttributeInspector"] = GatewayRole.CallerAttributeInspector,
        ["CatalogArtifactUploader"] = GatewayRole.CatalogArtifactUploader,
        ["DataProductAdmin"] = GatewayRole.DataProductAdmin,
        ["DataProductSync"] = GatewayRole.DataProductSync
    };

    /// <summary>
    /// Lenient parser: strips any tenant prefix (everything up to the last ':') before the lookup. Must NOT be used for authorization
    /// decisions on raw claim values; use <see cref="TryParseGlobalRole"/> or <see cref="TryParseTenantScopedRole"/> instead.
    /// </summary>
    public static bool TryParseRole(string? roleName, out GatewayRole role)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            role = default;
            return false;
        }

        // Support tenant-prefixed roles: "tenant-id:Consumer"
        var colonIdx = roleName.LastIndexOf(':', StringComparison.Ordinal);
        var lookupName = colonIdx >= 0 ? roleName[(colonIdx + 1)..] : roleName;

        return NameToRole.TryGetValue(lookupName.Trim(), out role);
    }

    /// <summary>
    /// Strict parser for a global role value: trims, rejects any value containing ':' and performs an exact name lookup (no prefix stripping).
    /// </summary>
    public static bool TryParseGlobalRole(string? roleName, out GatewayRole role)
    {
        role = default;
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return false;
        }

        var trimmed = roleName.Trim();
        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return NameToRole.TryGetValue(trimmed, out role);
    }

    /// <summary>
    /// Strict parser for a tenant-scoped role value "tenant:Role": requires exactly one ':' with a non-empty tenant part and an exactly
    /// matching role name. Anything else (leading colon, multiple colons, empty parts) is rejected.
    /// </summary>
    public static bool TryParseTenantScopedRole(string? raw, out string tenant, out GatewayRole role)
    {
        tenant = string.Empty;
        role = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var colonIdx = raw.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx <= 0 || colonIdx != raw.LastIndexOf(':', StringComparison.Ordinal))
        {
            return false;
        }

        var tenantPart = raw[..colonIdx].Trim();
        var rolePart = raw[(colonIdx + 1)..].Trim();
        if (tenantPart.Length == 0 || rolePart.Length == 0)
        {
            return false;
        }

        if (!NameToRole.TryGetValue(rolePart, out role))
        {
            return false;
        }

        tenant = tenantPart;
        return true;
    }

    /// <summary>
    /// True for the roles that exist only as tenant-scoped grants ("tenant:Role") and are implied by no other role: a global claim never
    /// confers them and <see cref="Implies"/> never derives them (not even from ClusterAdmin).
    /// </summary>
    public static bool IsTenantScopedOnly(this GatewayRole role) =>
        role is GatewayRole.DataProductAdmin or GatewayRole.DataProductSync;

    /// <summary>
    /// Checks role hierarchy: returns true if the granted role satisfies the required role.
    /// </summary>
    public static bool Implies(this GatewayRole granted, GatewayRole required)
    {
        if (granted == required)
        {
            return true;
        }

        // Data products: the product roles can expose data, so no role implies them, ClusterAdmin included.
        if (required.IsTenantScopedOnly())
        {
            return false;
        }

        // ClusterAdmin implies IT-administration, auditing, publishing, consumption, and catalog artifact upload.
        // It does NOT imply GovernanceAdmin, Filter*, CallerAttribute*, or DataProduct* roles.
        if (granted == GatewayRole.ClusterAdmin)
        {
            return required is GatewayRole.TenantAdmin
                or GatewayRole.SecurityAuditor
                or GatewayRole.SchemaPublisher
                or GatewayRole.Consumer
                or GatewayRole.CatalogArtifactUploader;
        }

        // TenantAdmin implies auditing, publishing, and consumer within tenant scope.
        // It does NOT imply GovernanceAdmin, ClusterAdmin, or dedicated filter/caller/product roles.
        if (granted == GatewayRole.TenantAdmin)
        {
            return required is GatewayRole.SecurityAuditor
                or GatewayRole.SchemaPublisher
                or GatewayRole.Consumer;
        }

        // GovernanceAdmin implies auditing, publishing, and consumer.
        if (granted == GatewayRole.GovernanceAdmin)
        {
            return required is GatewayRole.SecurityAuditor
                or GatewayRole.SchemaPublisher
                or GatewayRole.Consumer;
        }

        // SchemaPublisher and SecurityAuditor imply Consumer.
        if (granted is GatewayRole.SchemaPublisher or GatewayRole.SecurityAuditor)
        {
            return required is GatewayRole.Consumer;
        }

        return false;
    }
}
