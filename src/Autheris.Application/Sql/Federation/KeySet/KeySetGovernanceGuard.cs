namespace Autheris.Application.Sql.Federation.KeySet;

using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// WP-124-2: Zero-Trust Governance Guards for Dynamic Key-Set Filters.
/// Enforces:
/// - SEC-DATA124-01: Cleartext join key invariant (both sides must be ColumnAccessLevel.Clear; SEC-FED-19).
/// - SEC-DATA124-02: Explicit data-owner consent (AllowKeySetImport == true on target DataSourceConnectionOptions).
/// </summary>
public static class KeySetGovernanceGuard
{
    /// <summary>
    /// SEC-DATA124-01: Verifies that both join key columns have ColumnAccessLevel.Clear.
    /// Any column classified as Mask, MaskWithHmac, Redact, or Deny strictly blocks key-set pushdown (SEC-FED-19).
    /// </summary>
    public static bool AreJoinKeysClear(
        TableMetadata drivingMeta,
        TableAccessDecision drivingDecision,
        string drivingKeyColumn,
        TableMetadata drivenMeta,
        TableAccessDecision drivenDecision,
        string drivenKeyColumn)
    {
        ArgumentNullException.ThrowIfNull(drivingMeta);
        ArgumentNullException.ThrowIfNull(drivingDecision);
        ArgumentNullException.ThrowIfNull(drivenMeta);
        ArgumentNullException.ThrowIfNull(drivenDecision);

        var drivingCol = drivingMeta.GetColumn(drivingKeyColumn);
        if (drivingCol == null || drivingDecision.GetEffectiveColumnAccess(drivingCol.ColumnName, drivingMeta) != ColumnAccessLevel.Clear)
        {
            return false;
        }

        var drivenCol = drivenMeta.GetColumn(drivenKeyColumn);
        if (drivenCol == null || drivenDecision.GetEffectiveColumnAccess(drivenCol.ColumnName, drivenMeta) != ColumnAccessLevel.Clear)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// SEC-DATA124-02: Explicit Data-Owner Consent.
    /// Dynamic key import into the target source requires explicit opt-in via AllowKeySetImport == true.
    /// Default is false (fail closed).
    /// </summary>
    public static bool IsKeySetImportAllowed(DataSourceConnectionOptions? targetConnectionOptions)
    {
        return targetConnectionOptions?.AllowKeySetImport == true;
    }

    /// <summary>
    /// Evaluates both cleartext key invariant and data-owner consent.
    /// </summary>
    public static bool CanApplyKeySetPushdown(
        TableMetadata drivingMeta,
        TableAccessDecision drivingDecision,
        string drivingKeyColumn,
        TableMetadata drivenMeta,
        TableAccessDecision drivenDecision,
        string drivenKeyColumn,
        DataSourceConnectionOptions? targetConnectionOptions)
    {
        if (!IsKeySetImportAllowed(targetConnectionOptions))
        {
            return false;
        }

        return AreJoinKeysClear(
            drivingMeta,
            drivingDecision,
            drivingKeyColumn,
            drivenMeta,
            drivenDecision,
            drivenKeyColumn);
    }
}
