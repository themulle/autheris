using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Issue #107 (PLAN-GOV-107 C-4, C-5): shared read/write of the row filter evaluation settings for all governance engines.
/// Object level lives in <c>TABLE_ROW_FILTER_STRATEGIES</c> (separate from <c>TABLES</c> because ingestion upserts rewrite it), rule level in
/// three nullable columns of <c>CONSENT_ROW_FILTERS</c>. The callers own the transaction and the epoch bump (one transaction, SEC-RF107-14e);
/// no value-range validation happens here (caps are configuration, validated by the admin service and again on read).
/// </summary>
internal static class RowFilterEvaluationStore
{
    internal const string StrategyTable = "TABLE_ROW_FILTER_STRATEGIES";

    /// <summary>The stored settings of a table, or null when it has no row (= inherit everything).</summary>
    internal static async Task<RowFilterEvaluationSettings?> ReadObjectAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        Enlist(cmd, tx);
        cmd.CommandText = $"SELECT strategy, duration_seconds, max_keys FROM {dialect.Prefix}{StrategyTable} WHERE table_id = @tid";
        AddParameter(cmd, "@tid", tableId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadSettings(reader, 0);
    }

    /// <summary>
    /// Replaces the object-level settings of one table (null or all-null deletes the row) and bumps the table epoch through
    /// <paramref name="bumpEpoch"/> inside the caller's transaction. Null when the table is unknown.
    /// </summary>
    internal static async Task<RowFilterEvaluationChange?> SetObjectAsync(
        DbConnection conn,
        DbTransaction tx,
        KeyDefinitionDialect dialect,
        TableIdentifier table,
        RowFilterEvaluationSettings? settings,
        Sid actor,
        Func<Task<long>> bumpEpoch,
        CancellationToken ct)
    {
        Guid tableId;
        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = dialect.TableLookupSql;
            AddParameter(cmd, "@domain", table.Domain);
            AddParameter(cmd, "@schema", table.Schema);
            AddParameter(cmd, "@table", table.TableName);
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (id is null || !Guid.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture), out tableId))
            {
                return null;
            }
        }

        var before = await ReadObjectAsync(conn, tx, dialect, tableId, ct).ConfigureAwait(false) ?? RowFilterEvaluationSettings.Inherit;
        var after = settings ?? RowFilterEvaluationSettings.Inherit;

        if (after == RowFilterEvaluationSettings.Inherit)
        {
            await ExecAsync(conn, tx, $"DELETE FROM {dialect.Prefix}{StrategyTable} WHERE table_id = @tid", ct, ("@tid", tableId.ToString())).ConfigureAwait(false);
        }
        else
        {
            var now = ReferenceEquals(dialect, CatalogKeyDefinitionStore.Sqlite)
                ? (object)DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : DateTimeOffset.UtcNow;
            var parameters = new (string, object)[]
            {
                ("@tid", tableId.ToString()),
                ("@strategy", after.Strategy is { } s ? (object)(int)s : DBNull.Value),
                ("@duration", (object?)after.DurationSeconds ?? DBNull.Value),
                ("@maxkeys", (object?)after.MaxKeys ?? DBNull.Value),
                ("@by", actor.Value),
                ("@now", now)
            };
            var updated = await ExecAsync(conn, tx,
                $"UPDATE {dialect.Prefix}{StrategyTable} SET strategy = @strategy, duration_seconds = @duration, max_keys = @maxkeys, updated_by = @by, updated_at = @now WHERE table_id = @tid",
                ct, parameters).ConfigureAwait(false);
            if (updated == 0)
            {
                await ExecAsync(conn, tx,
                    $"INSERT INTO {dialect.Prefix}{StrategyTable} (table_id, strategy, duration_seconds, max_keys, updated_by, updated_at) VALUES (@tid, @strategy, @duration, @maxkeys, @by, @now)",
                    ct, parameters).ConfigureAwait(false);
            }
        }

        var epoch = await bumpEpoch().ConfigureAwait(false);
        return new RowFilterEvaluationChange(table, null, null, before, after, epoch);
    }

    /// <summary>The identity-relevant part of one stored rule row.</summary>
    internal sealed record RuleRow(
        ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table);

    /// <summary>
    /// One row filter with the fields the admin validation needs (type, temporal columns, key-set settings), its consent effect, tenant and
    /// target table; null when it does not exist.
    /// </summary>
    internal static async Task<RuleRow?> ReadRuleAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid rowFilterId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        Enlist(cmd, tx);
        cmd.CommandText = $@"SELECT f.id, f.consent_id, f.filter_group, f.column_name, f.filter_type, f.dependent_table,
                                    f.target_temporal_column, f.dependent_valid_from_column, f.dependent_valid_to_column,
                                    f.key_set_strategy, f.key_set_duration_seconds, f.key_set_max_keys,
                                    c.effect, c.tenant_id, t.source_name, t.schema_name, t.table_name
                             FROM {dialect.Prefix}CONSENT_ROW_FILTERS f
                             JOIN {dialect.Prefix}CONSENTS c ON c.id = f.consent_id
                             JOIN {dialect.Prefix}TABLES t ON t.id = c.table_id
                             WHERE f.id = @id";
        AddParameter(cmd, "@id", rowFilterId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        TableIdentifier? dependent = !reader.IsDBNull(5) && TableIdentifier.TryParse(reader.GetString(5), out var parsed) ? parsed : null;
        var settings = ReadSettings(reader, 9);
        var filter = new ConsentRowFilter
        {
            Id = Guid.Parse(reader.GetString(0)),
            ConsentId = Guid.Parse(reader.GetString(1)),
            FilterGroup = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
            ColumnName = reader.GetString(3),
            FilterType = (RowFilterType)(reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)),
            DependentTable = dependent,
            TargetTemporalColumn = reader.IsDBNull(6) ? null : reader.GetString(6),
            DependentValidFromColumn = reader.IsDBNull(7) ? null : reader.GetString(7),
            DependentValidToColumn = reader.IsDBNull(8) ? null : reader.GetString(8),
            KeySetStrategy = settings.Strategy,
            KeySetDurationSeconds = settings.DurationSeconds,
            KeySetMaxKeys = settings.MaxKeys
        };

        // Fail closed: an unreadable effect is treated as DENY, which the admin validation rejects for key-set settings.
        var effect = Enum.TryParse<ConsentEffect>(reader.GetString(12), true, out var e) ? e : ConsentEffect.Deny;
        if (!TenantId.TryParse(reader.GetString(13), out var tenant))
        {
            return null;
        }

        return new RuleRow(filter, effect, tenant, new TableIdentifier(reader.GetString(14), reader.GetString(15), reader.GetString(16)));
    }

    /// <summary>
    /// Updates the three key-set columns of one row filter whose consent belongs to <paramref name="tenant"/>, and bumps the target table epoch
    /// through <paramref name="bumpEpoch"/>, inside the caller's transaction. Null when the filter does not exist or belongs to another tenant.
    /// </summary>
    internal static async Task<RowFilterEvaluationChange?> UpdateRuleAsync(
        DbConnection conn,
        DbTransaction tx,
        KeyDefinitionDialect dialect,
        Guid rowFilterId,
        TenantId tenant,
        RowFilterEvaluationSettings settings,
        Func<TableIdentifier, Task<long>> bumpEpoch,
        CancellationToken ct)
    {
        var row = await ReadRuleAsync(conn, tx, dialect, rowFilterId, ct).ConfigureAwait(false);
        if (row is null || !string.Equals(row.Tenant.Value, tenant.Value, StringComparison.Ordinal))
        {
            return null;
        }

        var before = new RowFilterEvaluationSettings(row.Filter.KeySetStrategy, row.Filter.KeySetDurationSeconds, row.Filter.KeySetMaxKeys);
        await ExecAsync(conn, tx,
            $"UPDATE {dialect.Prefix}CONSENT_ROW_FILTERS SET key_set_strategy = @strategy, key_set_duration_seconds = @duration, key_set_max_keys = @maxkeys WHERE id = @id",
            ct,
            ("@strategy", settings.Strategy is { } s ? (object)(int)s : DBNull.Value),
            ("@duration", (object?)settings.DurationSeconds ?? DBNull.Value),
            ("@maxkeys", (object?)settings.MaxKeys ?? DBNull.Value),
            ("@id", rowFilterId.ToString())).ConfigureAwait(false);

        var epoch = await bumpEpoch(row.Table).ConfigureAwait(false);
        return new RowFilterEvaluationChange(row.Table, row.Tenant, rowFilterId, before, settings, epoch);
    }

    private static RowFilterEvaluationSettings ReadSettings(DbDataReader reader, int offset)
    {
        RowFilterEvaluationStrategy? strategy = reader.IsDBNull(offset)
            ? null
            : (RowFilterEvaluationStrategy)Convert.ToInt32(reader.GetValue(offset), CultureInfo.InvariantCulture);
        int? duration = reader.IsDBNull(offset + 1) ? null : Convert.ToInt32(reader.GetValue(offset + 1), CultureInfo.InvariantCulture);
        int? maxKeys = reader.IsDBNull(offset + 2) ? null : Convert.ToInt32(reader.GetValue(offset + 2), CultureInfo.InvariantCulture);
        return new RowFilterEvaluationSettings(strategy, duration, maxKeys);
    }

    private static void Enlist(DbCommand cmd, DbTransaction? tx)
    {
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static async Task<int> ExecAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        Enlist(cmd, tx);
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(cmd, name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
