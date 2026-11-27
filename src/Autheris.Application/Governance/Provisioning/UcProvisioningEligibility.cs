namespace Autheris.Application.Governance.Provisioning;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

/// <summary>Stable reason codes for consents that are not provisioned to Unity Catalog.</summary>
public static class UcReasonCodes
{
    public const string DataSourceNotProvisioned = "DATA_SOURCE_NOT_PROVISIONED";
    public const string TenantNotProvisioned = "TENANT_NOT_PROVISIONED";
    public const string GrantKindNotSupported = "GRANT_KIND_NOT_SUPPORTED";
    public const string EffectNotSupported = "EFFECT_NOT_SUPPORTED";
    public const string GranteeNotSupported = "GRANTEE_NOT_SUPPORTED";
    public const string PatternGrant = "PATTERN_GRANT";
    public const string ColumnSetNotSupported = "COLUMN_SET_NOT_SUPPORTED";
    public const string ColumnRules = "COLUMN_RULES";
    public const string RowFilters = "ROW_FILTERS";
    public const string RowScope = "ROW_SCOPE";
    public const string WriteRules = "WRITE_RULES";
    public const string TableNotFound = "TABLE_NOT_FOUND";
    public const string DataSourceMismatch = "DATA_SOURCE_MISMATCH";
    public const string ObjectKindNotSupported = "OBJECT_KIND_NOT_SUPPORTED";
    public const string CatalogMismatch = "CATALOG_MISMATCH";
    public const string TableRowPolicy = "TABLE_ROW_POLICY";
    public const string AggregationPolicy = "AGGREGATION_POLICY";
    public const string HiddenColumns = "HIDDEN_COLUMNS";
    public const string ColumnProtection = "COLUMN_PROTECTION";
    public const string ProtectionUnknown = "PROTECTION_UNKNOWN";
    public const string ProtectionClass = "PROTECTION_CLASS";
    public const string InvalidIdentifier = "INVALID_IDENTIFIER";
    public const string SchemaNotAllowed = "SCHEMA_NOT_ALLOWED";
    public const string PrincipalUnresolved = "PRINCIPAL_UNRESOLVED";
    public const string PrincipalAmbiguous = "PRINCIPAL_AMBIGUOUS";
}

/// <summary>The table privilege a provisionable consent maps to; names are normalized (lower case).</summary>
public sealed record UcTablePrivilege(string Catalog, string Schema, string Table, string Privilege)
{
    public string FullName => Catalog + "." + Schema + "." + Table;
    public string SchemaFullName => Catalog + "." + Schema;
}

/// <summary>Either a provisionable <see cref="Target"/> or an unsupported consent with a <see cref="ReasonCode"/>.</summary>
public sealed record UcEligibility(UcTablePrivilege? Target, string? ReasonCode)
{
    public bool IsProvisionable => Target is not null;

    public static UcEligibility Provisionable(UcTablePrivilege target) =>
        new(target ?? throw new ArgumentNullException(nameof(target)), null);

    public static UcEligibility Unsupported(string reasonCode) => new(null, reasonCode);
}

/// <summary>
/// Decides whether a consent can be expressed exactly as a Unity Catalog privilege. A table-wide <c>SELECT</c> carries no
/// expiry, column, row or masking restriction, so the decision is an allowlist: only an allow data grant on one table, of the
/// provisioning tenant, without any column or row restriction on the consent or the table, below Confidential and inside an
/// allowed schema is provisionable. Every other or unknown construct is unsupported and stays enforced by the gateway only.
/// </summary>
public static class UcProvisioningEligibility
{
    public static UcEligibility Evaluate(
        string dataSource,
        DatabricksDataSourceOptions? databricks,
        Consent consent,
        TableMetadata? table,
        EffectiveProtection? protection)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (string.IsNullOrWhiteSpace(dataSource) || databricks?.Provisioning is not { } provisioning)
        {
            return UcEligibility.Unsupported(UcReasonCodes.DataSourceNotProvisioned);
        }

        return EvaluateConsent(provisioning, consent)
            ?? EvaluateTable(dataSource, databricks, consent, table)
            ?? EvaluateProtection(table!, protection)
            ?? EvaluateTarget(databricks, provisioning, table!);
    }

    private static UcEligibility? EvaluateConsent(DatabricksProvisioningOptions provisioning, Consent consent)
    {
        if (string.IsNullOrWhiteSpace(provisioning.Tenant) ||
            !string.Equals(consent.TenantId.Value, provisioning.Tenant.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return UcEligibility.Unsupported(UcReasonCodes.TenantNotProvisioned);
        }

        if (consent.GrantKind != PermissionGrantKind.Data)
        {
            return UcEligibility.Unsupported(UcReasonCodes.GrantKindNotSupported);
        }

        if (consent.Effect != ConsentEffect.Allow)
        {
            return UcEligibility.Unsupported(UcReasonCodes.EffectNotSupported);
        }

        if (consent.GranteeType is not (GranteeType.User or GranteeType.Group or GranteeType.ServicePrincipal) ||
            string.IsNullOrWhiteSpace(consent.GranteeSid?.Value))
        {
            return UcEligibility.Unsupported(UcReasonCodes.GranteeNotSupported);
        }

        if (consent.IsPatternGrant)
        {
            return UcEligibility.Unsupported(UcReasonCodes.PatternGrant);
        }

        if (consent.ColumnSetMode != ColumnSetMode.AllNonHidden || consent.TargetColumns is { Count: > 0 })
        {
            return UcEligibility.Unsupported(UcReasonCodes.ColumnSetNotSupported);
        }

        if (consent.ColumnRules.Count > 0)
        {
            return UcEligibility.Unsupported(UcReasonCodes.ColumnRules);
        }

        if (consent.RowFilters.Count > 0)
        {
            return UcEligibility.Unsupported(UcReasonCodes.RowFilters);
        }

        if (consent.RowScope.Count > 0)
        {
            return UcEligibility.Unsupported(UcReasonCodes.RowScope);
        }

        return consent.WriteRules.Count > 0 ? UcEligibility.Unsupported(UcReasonCodes.WriteRules) : null;
    }

    private static UcEligibility? EvaluateTable(string dataSource, DatabricksDataSourceOptions databricks, Consent consent, TableMetadata? table)
    {
        if (table is null || !table.Table.IsActive)
        {
            return UcEligibility.Unsupported(UcReasonCodes.TableNotFound);
        }

        if (!string.Equals(table.Identifier.Domain, dataSource, StringComparison.OrdinalIgnoreCase) ||
            !consent.TableIdentifier.Equals(table.Identifier) ||
            (consent.TableId != Guid.Empty && consent.TableId != table.Table.Id) ||
            (!string.IsNullOrWhiteSpace(consent.DataSource) && !string.Equals(consent.DataSource.Trim(), dataSource, StringComparison.OrdinalIgnoreCase)) ||
            table.DataSourceType != DataSourceType.Sql ||
            !DatabaseDialectExtensions.TryParseDialect(table.Table.SourceType, out var dialect) ||
            dialect != DatabaseDialect.Databricks)
        {
            return UcEligibility.Unsupported(UcReasonCodes.DataSourceMismatch);
        }

        if (table.Table.ObjectKind is not (DiscoveryObjectKind.Table or DiscoveryObjectKind.View))
        {
            return UcEligibility.Unsupported(UcReasonCodes.ObjectKindNotSupported);
        }

        if (!string.IsNullOrWhiteSpace(table.PhysicalDatabaseName) &&
            !string.Equals(table.PhysicalDatabaseName.Trim(), databricks.Catalog?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return UcEligibility.Unsupported(UcReasonCodes.CatalogMismatch);
        }

        if (!string.IsNullOrWhiteSpace(table.Table.RlsExpression) ||
            !table.RequiredFilterColumns.IsEmpty ||
            !table.RequiredFilterColumnGroups.IsEmpty ||
            table.TenantColumnName is not null)
        {
            return UcEligibility.Unsupported(UcReasonCodes.TableRowPolicy);
        }

        if (table.EffectiveMinGroupSize is not null || table.EffectiveSmallGroupStrategy is not null)
        {
            return UcEligibility.Unsupported(UcReasonCodes.AggregationPolicy);
        }

        foreach (var column in table.Columns)
        {
            if (column.DiscoveryHidden)
            {
                return UcEligibility.Unsupported(UcReasonCodes.HiddenColumns);
            }
        }

        return table.ColumnMaskingRules.Count > 0 || table.ColumnMaskOverrides.Count > 0 || table.ColumnClassifications.Count > 0
            ? UcEligibility.Unsupported(UcReasonCodes.ColumnProtection)
            : null;
    }

    private static UcEligibility? EvaluateProtection(TableMetadata table, EffectiveProtection? protection)
    {
        if (protection is null)
        {
            return UcEligibility.Unsupported(UcReasonCodes.ProtectionUnknown);
        }

        if (protection.TableClass.IsStricterThan(ProtectionClass.Internal))
        {
            return UcEligibility.Unsupported(UcReasonCodes.ProtectionClass);
        }

        if (!string.IsNullOrWhiteSpace(protection.TableMaskCategory))
        {
            return UcEligibility.Unsupported(UcReasonCodes.ColumnProtection);
        }

        foreach (var column in protection.Columns.Values)
        {
            if (column is null || column.EffectiveClass.IsStricterThan(ProtectionClass.Internal))
            {
                return UcEligibility.Unsupported(UcReasonCodes.ProtectionClass);
            }

            if (!string.IsNullOrWhiteSpace(column.EffectiveMaskCategory))
            {
                return UcEligibility.Unsupported(UcReasonCodes.ColumnProtection);
            }
        }

        // Every column of the table must carry a protection entry; a column the snapshot does not know is not provably below Confidential.
        foreach (var column in table.Columns)
        {
            if (!protection.Columns.ContainsKey(column.ColumnName))
            {
                return UcEligibility.Unsupported(UcReasonCodes.ProtectionUnknown);
            }
        }

        return null;
    }

    private static UcEligibility EvaluateTarget(DatabricksDataSourceOptions databricks, DatabricksProvisioningOptions provisioning, TableMetadata table)
    {
        if (!UcSecurableNames.TryNormalizeSegment(databricks.Catalog, out var catalog) ||
            !UcSecurableNames.TryNormalizeSegment(table.Identifier.Schema, out var schema) ||
            !UcSecurableNames.TryNormalizeSegment(table.Identifier.TableName, out var name))
        {
            return UcEligibility.Unsupported(UcReasonCodes.InvalidIdentifier);
        }

        var allowed = UcSecurableNames.AllowedSchemaSet(provisioning.AllowedSchemas);
        return allowed.Contains(catalog + "." + schema)
            ? UcEligibility.Provisionable(new UcTablePrivilege(catalog, schema, name, UcPrivileges.Select))
            : UcEligibility.Unsupported(UcReasonCodes.SchemaNotAllowed);
    }
}
