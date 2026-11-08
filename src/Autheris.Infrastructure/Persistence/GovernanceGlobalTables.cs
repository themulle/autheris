namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Closed allowlist of global (non-tenant-scoped) governance tables (SEC-SIMPL-22).
/// Only tables WITHOUT a tenant_id column in the baseline schema may be listed. All tables not listed here are strictly tenant-scoped and require a non-null TenantId.
/// </summary>
public static class GovernanceGlobalTables
{
    private static readonly HashSet<string> GlobalTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLES", // Platform-wide catalog table registry
        "TABLE_COLUMNS", // Column definitions of registered catalog tables
        "COLUMN_MASKING_RULES", // Masking policies per table column
        "DATA_OWNERS", // Platform data ownership assignments
        "TABLE_OWNERS", // Table owner mappings
        "DATA_OWNER_DELEGATIONS", // Data owner delegations
        "ROLES", // Platform role definitions
        "ROLE_MEMBERS", // Role membership assignments
        "POLICY_EPOCHS", // Global policy version epochs
        "AUDIT_LOG_TRUNCATE_GUARD", // Single-row database truncate guard
        "CATALOG_STATE", // Global schema and catalog state tracking
        "TABLE_RELATIONS", // Cross-table relations and foreign keys
        "TABLE_RELATION_KEYS", // Relation key mappings
        "TABLE_INDEXES", // Physical index metadata
        "TABLE_INDEX_COLUMNS", // Physical index columns
        "TABLE_PRIMARY_KEY_COLUMNS", // Table primary key metadata
        "TABLE_TAGS", // Tags for catalog tables
        "TABLE_CATALOG_SETTINGS", // Global catalog configuration settings
        "TABLE_ROW_FILTER_STRATEGIES", // Row filter strategies
        "TRUSTED_DOCUMENT_APPS", // Registered trusted document applications
        "TRUSTED_DOCUMENT_APP_VERSIONS", // Trusted document app versions
        "TRUSTED_DOCUMENT_APP_ORIGINS", // Allowed origins for trusted apps
        "TRUSTED_DOCUMENT_HASHES", // Pre-approved document query hashes
        "TRUSTED_DOCUMENT_GENERATION", // Generation timestamps for trusted docs
    };

    public static IReadOnlySet<string> All => GlobalTables;

    public static bool IsGlobal(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return GlobalTables.Contains(tableName);
    }
}

/// <summary>
/// Tenant scope validator and builder (SEC-SIMPL-22).
/// Enforces that every tenant-scoped statement receives a non-null, non-empty TenantId.
/// </summary>
public static class TenantScope
{
    public static TenantId RequireTenant(TenantId tenantId, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (GovernanceGlobalTables.IsGlobal(tableName))
        {
            return tenantId;
        }

        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new ArgumentException($"TenantId is required for tenant-scoped table '{tableName}'.", nameof(tenantId));
        }

        return tenantId;
    }

    public static TenantId RequireTenant(TenantId? tenantId, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (GovernanceGlobalTables.IsGlobal(tableName))
        {
            return tenantId ?? default;
        }

        if (tenantId is null || string.IsNullOrWhiteSpace(tenantId.Value.Value))
        {
            throw new ArgumentException($"TenantId is required for tenant-scoped table '{tableName}'.", nameof(tenantId));
        }

        return tenantId.Value;
    }

    public static string RequireTenantString(string? tenantId, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (GovernanceGlobalTables.IsGlobal(tableName))
        {
            return tenantId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException($"TenantId is required for tenant-scoped table '{tableName}'.", nameof(tenantId));
        }

        return tenantId;
    }
}
