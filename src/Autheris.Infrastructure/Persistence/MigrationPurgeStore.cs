using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Reads archived ledger heads, unaudited purge reports created by migration steps, and the applied governance schema version.
/// </summary>
public class MigrationPurgeStore
{
    public const string PermissionRequestsPurgeStep = "0041_permission_requests_and_grant_kinds";

    private const string ExistsPurgeTablePgSql = "SELECT CASE WHEN to_regclass('migration_purge_report') IS NULL THEN 0 ELSE 1 END";
    private const string ExistsPurgeTableSsSql = "SELECT CASE WHEN OBJECT_ID(N'dbo.MIGRATION_PURGE_REPORT', N'U') IS NULL THEN 0 ELSE 1 END";

    private const string SelectUnauditedPgSql =
        "SELECT id, step, table_name, row_count, ledger_head_hash, purged_at, audited_at " +
        "FROM migration_purge_report WHERE step = @step AND audited_at IS NULL ORDER BY id";

    private const string SelectUnauditedSsSql =
        "SELECT id, step, table_name, row_count, ledger_head_hash, purged_at, audited_at " +
        "FROM dbo.MIGRATION_PURGE_REPORT WHERE step = @step AND audited_at IS NULL ORDER BY id";

    private const string ExistsArchiveTablePgSql = "SELECT CASE WHEN to_regclass('grant_record_heads_archive') IS NULL THEN 0 ELSE 1 END";
    private const string ExistsArchiveTableSsSql = "SELECT CASE WHEN OBJECT_ID(N'dbo.GRANT_RECORD_HEADS_ARCHIVE', N'U') IS NULL THEN 0 ELSE 1 END";

    private const string SelectArchivedHeadsPgSql =
        "SELECT tenant_id, last_seq, last_hash, archived_at, step " +
        "FROM grant_record_heads_archive WHERE step = @step ORDER BY tenant_id";

    private const string SelectArchivedHeadsSsSql =
        "SELECT tenant_id, last_seq, last_hash, archived_at, step " +
        "FROM dbo.GRANT_RECORD_HEADS_ARCHIVE WHERE step = @step ORDER BY tenant_id";

    private const string MarkAuditedPgSql =
        "UPDATE migration_purge_report SET audited_at = @auditedAt WHERE step = @step AND audited_at IS NULL";

    private const string MarkAuditedSsSql =
        "UPDATE dbo.MIGRATION_PURGE_REPORT SET audited_at = @auditedAt WHERE step = @step AND audited_at IS NULL";

    private const string ExistsSchemaMigrationsPgSql = "SELECT CASE WHEN to_regclass('autheris_schema_migrations') IS NULL THEN 0 ELSE 1 END";
    private const string ExistsSchemaMigrationsSsSql = "SELECT CASE WHEN OBJECT_ID(N'dbo.autheris_schema_migrations', N'U') IS NULL THEN 0 ELSE 1 END";

    private const string SelectMaxVersionPgSql = "SELECT COALESCE(MAX(version), 0) FROM autheris_schema_migrations";
    private const string SelectMaxVersionSsSql = "SELECT COALESCE(MAX(version), 0) FROM dbo.autheris_schema_migrations";

    private readonly IGovernanceSqlDialect? _dialect;
    private readonly TimeProvider _time;

    protected MigrationPurgeStore()
    {
        _time = TimeProvider.System;
    }

    public MigrationPurgeStore(IGovernanceSqlDialect dialect, TimeProvider? timeProvider = null)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _time = timeProvider ?? TimeProvider.System;
    }

    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public virtual async Task<IReadOnlyList<MigrationPurgeReportEntry>> GetUnauditedPurgeReportsAsync(
        string stepName = PermissionRequestsPurgeStep,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        if (!await TableExistsAsync(conn, IsSqlServer ? ExistsPurgeTableSsSql : ExistsPurgeTablePgSql, ct).ConfigureAwait(false))
        {
            return [];
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? SelectUnauditedSsSql : SelectUnauditedPgSql;
        cmd.AddBound("@step", stepName.Trim());

        var rows = new List<MigrationPurgeReportEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MigrationPurgeReportEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                ConsentWorkflowSql.ReadOptionalString(reader, 4),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 5) ?? _time.GetUtcNow(),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6)));
        }

        return rows;
    }

    public virtual async Task<IReadOnlyList<GrantRecordHeadArchiveEntry>> GetArchivedHeadsAsync(
        string stepName = PermissionRequestsPurgeStep,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        if (!await TableExistsAsync(conn, IsSqlServer ? ExistsArchiveTableSsSql : ExistsArchiveTablePgSql, ct).ConfigureAwait(false))
        {
            return [];
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? SelectArchivedHeadsSsSql : SelectArchivedHeadsPgSql;
        cmd.AddBound("@step", stepName.Trim());

        var rows = new List<GrantRecordHeadArchiveEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GrantRecordHeadArchiveEntry(
                reader.GetString(4),
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 3) ?? _time.GetUtcNow()));
        }

        return rows;
    }

    public virtual async Task MarkPurgeReportsAuditedAsync(
        string stepName,
        DateTimeOffset auditedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        if (!await TableExistsAsync(conn, IsSqlServer ? ExistsPurgeTableSsSql : ExistsPurgeTablePgSql, ct).ConfigureAwait(false))
        {
            return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? MarkAuditedSsSql : MarkAuditedPgSql;
        cmd.AddBound("@step", stepName.Trim());
        cmd.AddBound("@auditedAt", auditedAt.ToUniversalTime(), DbType.DateTimeOffset);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public virtual async Task<int> GetAppliedSchemaMaxVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        if (!await TableExistsAsync(conn, IsSqlServer ? ExistsSchemaMigrationsSsSql : ExistsSchemaMigrationsPgSql, ct).ConfigureAwait(false))
        {
            return 0;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? SelectMaxVersionSsSql : SelectMaxVersionPgSql;
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    public static string ComputeArchivedHeadDigest(IReadOnlyList<GrantRecordHeadArchiveEntry> heads)
    {
        if (heads is null || heads.Count == 0)
        {
            return "EMPTY";
        }

        var builder = new StringBuilder();
        foreach (var entry in heads.OrderBy(h => h.TenantId, StringComparer.Ordinal))
        {
            builder.Append(entry.TenantId)
                .Append(':')
                .Append(entry.LastSeq.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(entry.LastHash)
                .Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    private static async Task<bool> TableExistsAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is not null and not DBNull && Convert.ToInt32(scalar, CultureInfo.InvariantCulture) == 1;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        if (_dialect is null)
        {
            throw new InvalidOperationException("Dialect is not configured for MigrationPurgeStore.");
        }

        var conn = _dialect.CreateConnection();
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
