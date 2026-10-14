namespace Autheris.Domain.Options;

/// <summary>
/// SR-L-09: one-time migration switch for SQLite governance databases that still contain legacy audit rows without a sequence
/// number (written before the sequenced hash chain). Bound to the <c>Audit</c> configuration section, standalone from
/// <see cref="GatewayOptions"/>.
/// </summary>
public sealed class AuditLegacyMigrationOptions
{
    public const string SectionName = "Gateway:Audit";

    /// <summary>
    /// When <c>true</c>, legacy audit rows with a <c>NULL</c> sequence are re-sealed once under the real audit key (sequence numbers
    /// assigned in rowid order) and the table is backed up to <c>AUDIT_LOG_ENTRIES_legacy_backup</c> first. When <c>false</c>
    /// (default) such rows are treated as an audit chain integrity violation. Set it back to <c>false</c> after the migration.
    /// </summary>
    public bool AllowLegacyUnsequencedRows { get; init; }
}
