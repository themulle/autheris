using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL and SQL Server persistence for tenant-scoped table write policies (<c>table_write_policies</c>, issue #1154).
/// </summary>
public sealed class TableWritePolicyStore(
    GovernanceRepository repository,
    IGovernanceSqlDialect dialect,
    TimeProvider? timeProvider = null) : ITableWritePolicyRepository
{
    private const string SelectColumns =
        "tenant_id, table_id, mode, allowed_operations_json, writable_columns_json, require_pk_predicate, require_row_filter, max_rows_per_statement, server_stamped_columns_json, updated_by, updated_at";

    private const string GetPgSql = "SELECT " + SelectColumns + " FROM table_write_policies WHERE tenant_id = @tenantId AND table_id = @tableId";
    private const string GetSsSql = "SELECT " + SelectColumns + " FROM dbo.TABLE_WRITE_POLICIES WHERE tenant_id = @tenantId AND table_id = @tableId";

    private const string ListPgSql = "SELECT " + SelectColumns + " FROM table_write_policies WHERE tenant_id = @tenantId ORDER BY table_id";
    private const string ListSsSql = "SELECT " + SelectColumns + " FROM dbo.TABLE_WRITE_POLICIES WHERE tenant_id = @tenantId ORDER BY table_id";

    private const string UpsertPgSql =
        "INSERT INTO table_write_policies (" + SelectColumns + ") VALUES (@tenantId, @tableId, @mode, @allowedOps, @writableCols, @requirePk, @requireRowFilter, @maxRows, @stampedCols, @updatedBy, @updatedAt) " +
        "ON CONFLICT (tenant_id, table_id) DO UPDATE SET mode = EXCLUDED.mode, allowed_operations_json = EXCLUDED.allowed_operations_json, writable_columns_json = EXCLUDED.writable_columns_json, " +
        "require_pk_predicate = EXCLUDED.require_pk_predicate, require_row_filter = EXCLUDED.require_row_filter, max_rows_per_statement = EXCLUDED.max_rows_per_statement, " +
        "server_stamped_columns_json = EXCLUDED.server_stamped_columns_json, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at";

    private const string UpsertSsSql =
        "MERGE dbo.TABLE_WRITE_POLICIES WITH (HOLDLOCK) AS target USING (VALUES (@tenantId, @tableId)) AS source (tenant_id, table_id) " +
        "ON target.tenant_id = source.tenant_id AND target.table_id = source.table_id " +
        "WHEN MATCHED THEN UPDATE SET mode = @mode, allowed_operations_json = @allowedOps, writable_columns_json = @writableCols, " +
        "require_pk_predicate = @requirePk, require_row_filter = @requireRowFilter, max_rows_per_statement = @maxRows, " +
        "server_stamped_columns_json = @stampedCols, updated_by = @updatedBy, updated_at = @updatedAt " +
        "WHEN NOT MATCHED THEN INSERT (" + SelectColumns + ") VALUES (@tenantId, @tableId, @mode, @allowedOps, @writableCols, @requirePk, @requireRowFilter, @maxRows, @stampedCols, @updatedBy, @updatedAt);";

    private const string DeletePgSql = "DELETE FROM table_write_policies WHERE tenant_id = @tenantId AND table_id = @tableId";
    private const string DeleteSsSql = "DELETE FROM dbo.TABLE_WRITE_POLICIES WHERE tenant_id = @tenantId AND table_id = @tableId";

    private readonly GovernanceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public async Task<TableWritePolicy?> GetAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
    {
        if (tableId == Guid.Empty)
        {
            return null;
        }

        var tenantValue = RequireTenant(tenant);
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? GetSsSql : GetPgSql;
        cmd.AddBound("@tenantId", tenantValue);
        cmd.AddBound("@tableId", tableId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    public async Task<IReadOnlyList<TableWritePolicy>> ListAsync(TenantId tenant, CancellationToken ct = default)
    {
        var tenantValue = RequireTenant(tenant);
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? ListSsSql : ListPgSql;
        cmd.AddBound("@tenantId", tenantValue);
        var list = new List<TableWritePolicy>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadRow(reader));
        }

        return list;
    }

    public async Task<TableWritePolicy> UpsertAsync(TableWritePolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var tenantValue = RequireTenant(policy.Tenant);
        var now = policy.UpdatedAt == default ? _time.GetUtcNow().ToUniversalTime() : policy.UpdatedAt.ToUniversalTime();
        var normalized = policy with { UpdatedAt = now };

        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? UpsertSsSql : UpsertPgSql;
        cmd.AddBound("@tenantId", tenantValue);
        cmd.AddBound("@tableId", normalized.TableId.ToString("D"));
        cmd.AddBound("@mode", normalized.Mode.ToString());
        cmd.AddBound("@allowedOps", JsonSerializer.Serialize(normalized.AllowedOperations.Select(static o => o.ToString()).ToArray()));
        cmd.AddBound("@writableCols", normalized.WritableColumns is { Count: > 0 } cols ? JsonSerializer.Serialize(cols) : (object)DBNull.Value, DbType.String);
        cmd.AddBound("@requirePk", normalized.RequirePrimaryKeyPredicate, DbType.Boolean);
        cmd.AddBound("@requireRowFilter", normalized.RequireRowFilter, DbType.Boolean);
        cmd.AddBound("@maxRows", normalized.MaxRowsPerStatement.HasValue ? normalized.MaxRowsPerStatement.Value : (object)DBNull.Value, DbType.Int64);
        cmd.AddBound("@stampedCols", normalized.ServerStampedColumns is { Count: > 0 } stamped ? JsonSerializer.Serialize(stamped) : (object)DBNull.Value, DbType.String);
        cmd.AddBound("@updatedBy", string.IsNullOrWhiteSpace(normalized.UpdatedBy) ? "system" : normalized.UpdatedBy);
        cmd.AddBound("@updatedAt", now, DbType.DateTimeOffset);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return normalized;
    }

    public async Task<bool> DeleteAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
    {
        if (tableId == Guid.Empty)
        {
            return false;
        }

        var tenantValue = RequireTenant(tenant);
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? DeleteSsSql : DeletePgSql;
        cmd.AddBound("@tenantId", tenantValue);
        cmd.AddBound("@tableId", tableId.ToString("D"));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static string RequireTenant(TenantId tenant) =>
        TenantScope.RequireTenant(tenant, "table_write_policies").Value;

    private static TableWritePolicy ReadRow(DbDataReader reader)
    {
        var tenant = new TenantId(reader.GetString(0));
        var tableId = Guid.Parse(reader.GetString(1));
        var mode = Enum.TryParse<TableWriteMode>(reader.GetString(2), ignoreCase: true, out var parsedMode)
            ? parsedMode
            : TableWriteMode.ReadOnly;

        var rawOps = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        var ops = rawOps
            .Select(static s => Enum.TryParse<WriteOperation>(s, ignoreCase: true, out var op) ? op : (WriteOperation?)null)
            .Where(static o => o.HasValue)
            .Select(static o => o!.Value)
            .Distinct()
            .ToArray();

        IReadOnlyList<string>? writableCols = reader.IsDBNull(4)
            ? null
            : JsonSerializer.Deserialize<string[]>(reader.GetString(4));

        var requirePk = reader.GetBoolean(5);
        var requireRowFilter = reader.GetBoolean(6);
        long? maxRows = reader.IsDBNull(7) ? null : reader.GetInt64(7);

        IReadOnlyDictionary<string, string>? stampedCols = reader.IsDBNull(8)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8));

        var updatedBy = reader.GetString(9);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(10);

        return new TableWritePolicy(
            tenant,
            tableId,
            mode,
            ops,
            writableCols,
            requirePk,
            requireRowFilter,
            maxRows,
            stampedCols,
            updatedBy,
            updatedAt);
    }
}
