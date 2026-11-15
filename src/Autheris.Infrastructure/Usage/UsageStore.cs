namespace Autheris.Infrastructure.Usage;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>Parameterized PostgreSQL and SQL Server persistence implementation for usage accounting.</summary>
internal static class UsageStore
{
    private const int MaxPageSize = 500;
    private const int MaxPurgeBatch = 10_000;

    private static readonly string[] SeriesKeys = ["day", "series_hash"];
    private static readonly string[] SeriesCols =
        ["tenant_id", "user_key", "group_set_id", "billing_group", "transport", "application", "client_id", "data_source", "table_id"];
    private static readonly string[] SeriesCounters =
    [
        "requests", "queries", "rows_returned", "rows_measured", "bytes", "duration_ms", "analysis_cost",
        "source_compute_ms", "source_compute_measured", "source_cost_micros", "source_cost_measured",
        "allow_count", "deny_access", "deny_rate_limit", "deny_cost", "deny_quota", "deny_other", "errors"
    ];
    private static readonly string[] GrantKeys = ["day", "tenant_id", "grant_id", "data_source", "table_id"];
    private static readonly string[] GrantCounters =
        ["accesses", "queries", "rows_returned", "bytes", "analysis_cost", "source_compute_ms", "source_cost_micros"];
    private static readonly string[] GroupSetKeys = ["tenant_id", "group_set_id", "group_sid"];
    private static readonly string[] GroupSetCols = ["first_seen"];
    private static readonly string[] GrantRefKeys = ["tenant_id", "grant_id"];
    private static readonly string[] GrantRefCols = ["valid_from", "valid_until", "first_seen", "last_seen"];

    public static async Task AccumulateAsync(GovernanceRepository repo, UsageGenerationSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var k in snapshot.Series.Keys) TenantScope.RequireTenant(k.Tenant, "USAGE_DAILY");
        foreach (var k in snapshot.GrantSeries.Keys) TenantScope.RequireTenant(k.Tenant, "USAGE_GRANT_DAILY");
        foreach (var k in snapshot.GroupSets.Keys) TenantScope.RequireTenant(k.Tenant, "USAGE_GROUP_SET");
        foreach (var k in snapshot.GrantRefs.Keys) TenantScope.RequireTenant(k.Tenant, "USAGE_GRANT_REF");

        if (snapshot.Series.Count == 0 && snapshot.GrantSeries.Count == 0
            && snapshot.GroupSets.Count == 0 && snapshot.GrantRefs.Count == 0)
        {
            return;
        }

        var utcToday = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await UpsertGroupSetsAsync(repo, conn, tx, snapshot, utcToday, ct).ConfigureAwait(false);
            await UpsertGrantRefsAsync(repo, conn, tx, snapshot, utcToday, ct).ConfigureAwait(false);
            var seriesList = new List<(UsageSeriesKey, UsageSeriesCounters)>(snapshot.Series.Count);
            foreach (var kv in snapshot.Series) seriesList.Add((kv.Key, kv.Value));
            await UpsertSeriesEntriesAsync(repo, conn, tx, seriesList, ct).ConfigureAwait(false);
            await UpsertGrantSeriesAsync(repo, conn, tx, snapshot.GrantSeries, ct).ConfigureAwait(false);
            await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await repo.RollbackTransactionAsync(tx, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<UsageReportPage> QueryAsync(GovernanceRepository repo, UsageReportQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        TenantScope.RequireTenant(query.Tenant, query.GroupBy == UsageGroupBy.Grant ? "USAGE_GRANT_DAILY" : "USAGE_DAILY");
        if (query.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(query), "Limit must be positive.");
        int pageSize = Math.Min(query.Limit, MaxPageSize);

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.AddBound("@tenant_id", query.Tenant.Value);
        AddDate(cmd, "@from_day", query.FromUtc);
        AddDate(cmd, "@to_day", query.ToUtc);

        bool isGrant = query.GroupBy == UsageGroupBy.Grant;
        string alias = isGrant ? "gd" : "d";
        string fromSql = isGrant
            ? "USAGE_GRANT_DAILY gd"
            : query.GroupBy == UsageGroupBy.Group
                ? "USAGE_DAILY d INNER JOIN USAGE_GROUP_SET gs ON gs.tenant_id = d.tenant_id AND gs.group_set_id = d.group_set_id"
                : "USAGE_DAILY d";
        string dimCol = query.GroupBy switch
        {
            UsageGroupBy.Day => "d.day", UsageGroupBy.User => "d.user_key", UsageGroupBy.Group => "gs.group_sid",
            UsageGroupBy.BillingGroup => "d.billing_group", UsageGroupBy.DataSource => "d.data_source",
            UsageGroupBy.Table => "d.table_id", UsageGroupBy.Transport => "d.transport",
            UsageGroupBy.Application => "d.application", UsageGroupBy.ClientId => "d.client_id",
            UsageGroupBy.Grant => "gd.grant_id",
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.GroupBy, "Unknown UsageGroupBy.")
        };

        var where = new StringBuilder($"{alias}.tenant_id = @tenant_id AND {alias}.day >= @from_day AND {alias}.day <= @to_day");
        AppendFilters(cmd, where, query, alias, isGrant);
        AppendCursor(cmd, where, query.GroupBy, dimCol, query.Cursor);

        if (query.IncludeTariffBreakdown)
        {
            return await QueryBreakdownAsync(repo, cmd, fromSql, where, dimCol, alias, isGrant, query.GroupBy, pageSize, ct).ConfigureAwait(false);
        }

        string cols = isGrant
            ? $"{dimCol} AS dim_val, SUM(gd.accesses), SUM(gd.queries), SUM(gd.rows_returned), 0, SUM(gd.bytes), 0, SUM(gd.analysis_cost), SUM(gd.source_compute_ms), 0, SUM(gd.source_cost_micros), 0, SUM(gd.accesses), 0, 0, 0, 0, 0, 0"
            : $"{dimCol} AS dim_val, SUM(d.requests), SUM(d.queries), SUM(d.rows_returned), SUM(d.rows_measured), SUM(d.bytes), SUM(d.duration_ms), SUM(d.analysis_cost), SUM(d.source_compute_ms), SUM(d.source_compute_measured), SUM(d.source_cost_micros), SUM(d.source_cost_measured), SUM(d.allow_count), SUM(d.deny_access), SUM(d.deny_rate_limit), SUM(d.deny_cost), SUM(d.deny_quota), SUM(d.deny_other), SUM(d.errors)";

        cmd.AddBound("@limit", pageSize + 1, DbType.Int32);
        cmd.CommandText = repo.Dialect is PostgreSqlGovernanceDialect
            ? $"SELECT {cols} FROM {fromSql} WHERE {where} GROUP BY {dimCol} ORDER BY {dimCol} ASC LIMIT @limit"
            : $"SELECT TOP (@limit) {cols} FROM {fromSql} WHERE {where} GROUP BY {dimCol} ORDER BY {dimCol} ASC";

        var rows = new List<UsageReportRow>(pageSize + 1);
        await using var reader = await repo.ExecuteReaderAsync(cmd, "Usage.Query", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadReportRow(reader, query.GroupBy));

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeCursor(rows[^1], query.GroupBy);
        }

        return new UsageReportPage(rows, nextCursor);
    }

    private static async Task<UsageReportPage> QueryBreakdownAsync(
        GovernanceRepository repo, DbCommand cmd, string fromSql, StringBuilder where,
        string dimCol, string alias, bool isGrant, UsageGroupBy groupBy, int pageSize, CancellationToken ct)
    {
        const int breakdownRowLimit = 50_000;
        string sumCols = isGrant
            ? $"{alias}.day, {alias}.data_source, {dimCol} AS dim_val, SUM(gd.accesses), SUM(gd.queries), SUM(gd.rows_returned), 0, SUM(gd.bytes), 0, SUM(gd.analysis_cost), SUM(gd.source_compute_ms), 0, SUM(gd.source_cost_micros), 0, SUM(gd.accesses), 0, 0, 0, 0, 0, 0"
            : $"{alias}.day, {alias}.data_source, {dimCol} AS dim_val, SUM(d.requests), SUM(d.queries), SUM(d.rows_returned), SUM(d.rows_measured), SUM(d.bytes), SUM(d.duration_ms), SUM(d.analysis_cost), SUM(d.source_compute_ms), SUM(d.source_compute_measured), SUM(d.source_cost_micros), SUM(d.source_cost_measured), SUM(d.allow_count), SUM(d.deny_access), SUM(d.deny_rate_limit), SUM(d.deny_cost), SUM(d.deny_quota), SUM(d.deny_other), SUM(d.errors)";
        string baseGroup = groupBy == UsageGroupBy.Day ? $"{alias}.day, {alias}.data_source"
            : groupBy == UsageGroupBy.DataSource ? $"{alias}.data_source, {alias}.day"
            : $"{dimCol}, {alias}.day, {alias}.data_source";
        string allowGroup = isGrant ? string.Empty : ", CASE WHEN d.allow_count > 0 THEN 1 ELSE 0 END";
        string allowOrder = isGrant ? string.Empty : ", CASE WHEN d.allow_count > 0 THEN 1 ELSE 0 END ASC";
        string orderBy = $"{dimCol} ASC, {alias}.day ASC, {alias}.data_source ASC{allowOrder}";

        cmd.AddBound("@limit", breakdownRowLimit, DbType.Int32);
        cmd.CommandText = repo.Dialect is PostgreSqlGovernanceDialect
            ? $"SELECT {sumCols} FROM {fromSql} WHERE {where} GROUP BY {baseGroup}{allowGroup} ORDER BY {orderBy} LIMIT @limit"
            : $"SELECT TOP (@limit) {sumCols} FROM {fromSql} WHERE {where} GROUP BY {baseGroup}{allowGroup} ORDER BY {orderBy}";

        var rows = new List<UsageReportRow>();
        var distinctDims = new List<string>();
        string? currentDim = null;
        int rawRead = 0;

        await using var reader = await repo.ExecuteReaderAsync(cmd, "Usage.QueryBreakdown", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rawRead++;
            var row = ReadBreakdownReportRow(reader, groupBy);
            string dimKey = EncodeCursor(row, groupBy) ?? string.Empty;
            if (!string.Equals(dimKey, currentDim, StringComparison.Ordinal))
            {
                if (distinctDims.Count >= pageSize)
                {
                    return new UsageReportPage(rows, EncodeCursor(rows[^1], groupBy));
                }

                distinctDims.Add(dimKey);
                currentDim = dimKey;
            }

            rows.Add(row);
        }

        if (rawRead == breakdownRowLimit && distinctDims.Count > 1)
        {
            string lastDim = distinctDims[^1];
            int cut = rows.FindIndex(r => string.Equals(EncodeCursor(r, groupBy) ?? string.Empty, lastDim, StringComparison.Ordinal));
            if (cut > 0)
            {
                rows.RemoveRange(cut, rows.Count - cut);
                return new UsageReportPage(rows, EncodeCursor(rows[^1], groupBy));
            }
        }

        return new UsageReportPage(rows, null);
    }

    public static async Task<int> PurgeAsync(GovernanceRepository repo, DateOnly cutoffExclusive, int batchSize, CancellationToken ct)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        int effectiveBatch = Math.Min(batchSize, MaxPurgeBatch);
        int deleted = 0;

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            deleted += await PurgeTableAsync(repo, conn, tx, "USAGE_DAILY", "usage_daily", cutoffExclusive, effectiveBatch, ct).ConfigureAwait(false);
            deleted += await PurgeTableAsync(repo, conn, tx, "USAGE_GRANT_DAILY", "usage_grant_daily", cutoffExclusive, effectiveBatch, ct).ConfigureAwait(false);

            using (var gsCmd = conn.CreateCommand())
            {
                gsCmd.Transaction = tx;
                gsCmd.CommandText = "DELETE FROM USAGE_GROUP_SET WHERE NOT EXISTS (SELECT 1 FROM USAGE_DAILY d WHERE d.tenant_id = USAGE_GROUP_SET.tenant_id AND d.group_set_id = USAGE_GROUP_SET.group_set_id)";
                await repo.ExecuteNonQueryAsync(gsCmd, "Usage.PurgeGroupSets", ct).ConfigureAwait(false);
            }

            using (var grCmd = conn.CreateCommand())
            {
                grCmd.Transaction = tx;
                grCmd.CommandText = "DELETE FROM USAGE_GRANT_REF WHERE last_seen < @cutoff AND NOT EXISTS (SELECT 1 FROM USAGE_GRANT_DAILY gd WHERE gd.tenant_id = USAGE_GRANT_REF.tenant_id AND gd.grant_id = USAGE_GRANT_REF.grant_id)";
                AddDate(grCmd, "@cutoff", cutoffExclusive);
                await repo.ExecuteNonQueryAsync(grCmd, "Usage.PurgeGrantRefs", ct).ConfigureAwait(false);
            }

            await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await repo.RollbackTransactionAsync(tx, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<int> EraseUserAsync(GovernanceRepository repo, TenantId tenant, string userKey, CancellationToken ct)
    {
        TenantScope.RequireTenant(tenant, "USAGE_DAILY");
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        if (string.Equals(userKey, "erased", StringComparison.Ordinal)) return 0;

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = new List<(UsageSeriesKey, UsageSeriesCounters)>();
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = @"SELECT day, group_set_id, billing_group, transport, application, client_id, data_source, table_id,
                    requests, queries, rows_returned, rows_measured, bytes, duration_ms, analysis_cost,
                    source_compute_ms, source_compute_measured, source_cost_micros, source_cost_measured,
                    allow_count, deny_access, deny_rate_limit, deny_cost, deny_quota, deny_other, errors
                    FROM USAGE_DAILY WHERE tenant_id = @tenant_id AND user_key = @user_key";
                sel.AddBound("@tenant_id", tenant.Value);
                sel.AddBound("@user_key", userKey);
                await using var r = await repo.ExecuteReaderAsync(sel, "Usage.EraseSelect", ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = new UsageSeriesKey(tenant, ReadDate(r, 0), "erased", r.GetString(1), r.GetString(2),
                        (DataSourceTransportFlags)r.GetInt16(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7));
                    var cnt = UsageSeriesCounters.FromTotals(
                        r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13),
                        r.GetInt64(14), r.GetInt64(15), r.GetInt64(16), r.GetInt64(17), r.GetInt64(18), r.GetInt64(19),
                        r.GetInt64(20), r.GetInt64(21), r.GetInt64(22), r.GetInt64(23), r.GetInt64(24), r.GetInt64(25));
                    rows.Add((key, cnt));
                }
            }

            if (rows.Count > 0)
            {
                await UpsertSeriesEntriesAsync(repo, conn, tx, rows, ct).ConfigureAwait(false);
            }

            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM USAGE_DAILY WHERE tenant_id = @tenant_id AND user_key = @user_key";
            del.AddBound("@tenant_id", tenant.Value);
            del.AddBound("@user_key", userKey);
            int removed = await repo.ExecuteNonQueryAsync(del, "Usage.EraseDelete", ct).ConfigureAwait(false);
            await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return removed;
        }
        catch
        {
            await repo.RollbackTransactionAsync(tx, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(
        GovernanceRepository repo, TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct)
    {
        TenantScope.RequireTenant(tenant, "USAGE_TARIFF");
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tenant_id, data_source, version, valid_from, valid_until, base_per_query, per_row, per_mib,
            per_compute_second, per_analysis_cost, source_cost_factor, source_cost_unit, unit_label, compute_basis, created_by, created_at, reason
            FROM USAGE_TARIFF WHERE (tenant_id = @tenant_id OR tenant_id = '*') AND valid_from <= @to_utc AND (valid_until IS NULL OR valid_until > @from_utc)
            ORDER BY tenant_id, data_source, version";
        cmd.AddBound("@tenant_id", tenant.Value);
        AddDate(cmd, "@from_utc", fromUtc);
        AddDate(cmd, "@to_utc", toUtc);

        var list = new List<UsageTariffVersion>();
        await using var r = await repo.ExecuteReaderAsync(cmd, "Usage.GetTariffs", ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new UsageTariffVersion(
                r.GetString(0), r.GetString(1), r.GetInt32(2), ReadDate(r, 3), r.IsDBNull(4) ? null : ReadDate(r, 4),
                Convert.ToDecimal(r.GetValue(5), CultureInfo.InvariantCulture), Convert.ToDecimal(r.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToDecimal(r.GetValue(7), CultureInfo.InvariantCulture), Convert.ToDecimal(r.GetValue(8), CultureInfo.InvariantCulture),
                Convert.ToDecimal(r.GetValue(9), CultureInfo.InvariantCulture), Convert.ToDecimal(r.GetValue(10), CultureInfo.InvariantCulture),
                r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12), r.GetInt16(13), r.GetString(14),
                ReadDto(r, 15) ?? throw new InvalidOperationException("USAGE_TARIFF.created_at was null."), r.GetString(16)));
        }

        return list;
    }

    public static async Task<UsageTariffVersion> AddTariffVersionAsync(GovernanceRepository repo, UsageTariffVersion tariff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        TenantScope.RequireTenantString(tariff.TenantId, "USAGE_TARIFF");
        ArgumentException.ThrowIfNullOrWhiteSpace(tariff.DataSource);
        var createdAt = tariff.CreatedAt == default ? TimeProvider.System.GetUtcNow() : tariff.CreatedAt.ToUniversalTime();

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                int prev = 0;
                using (var lockCmd = conn.CreateCommand())
                {
                    lockCmd.Transaction = tx;
                    lockCmd.CommandText = repo.Dialect.LockRowsForUpdate.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
                        ? "SELECT version FROM USAGE_TARIFF WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant_id AND data_source = @data_source ORDER BY version DESC"
                        : $"SELECT version FROM USAGE_TARIFF WHERE tenant_id = @tenant_id AND data_source = @data_source ORDER BY version DESC {repo.Dialect.LockRowsForUpdate}";
                    lockCmd.AddBound("@tenant_id", tariff.TenantId);
                    lockCmd.AddBound("@data_source", tariff.DataSource);
                    await using var r = await repo.ExecuteReaderAsync(lockCmd, "Usage.TariffLockLatest", ct).ConfigureAwait(false);
                    if (await r.ReadAsync(ct).ConfigureAwait(false)) prev = r.GetInt32(0);
                }

                if (prev > 0)
                {
                    using var closeCmd = conn.CreateCommand();
                    closeCmd.Transaction = tx;
                    closeCmd.CommandText = "UPDATE USAGE_TARIFF SET valid_until = @valid_from WHERE tenant_id = @tenant_id AND data_source = @data_source AND version = @prev";
                    AddDate(closeCmd, "@valid_from", tariff.ValidFrom);
                    closeCmd.AddBound("@tenant_id", tariff.TenantId);
                    closeCmd.AddBound("@data_source", tariff.DataSource);
                    closeCmd.AddBound("@prev", prev, DbType.Int32);
                    await repo.ExecuteNonQueryAsync(closeCmd, "Usage.TariffClosePrevious", ct).ConfigureAwait(false);
                }

                int next = prev + 1;
                using (var insCmd = conn.CreateCommand())
                {
                    insCmd.Transaction = tx;
                    insCmd.CommandText = @"INSERT INTO USAGE_TARIFF (tenant_id, data_source, version, valid_from, valid_until, base_per_query, per_row, per_mib,
                        per_compute_second, per_analysis_cost, source_cost_factor, source_cost_unit, unit_label, compute_basis, created_by, created_at, reason)
                        VALUES (@tenant_id, @data_source, @version, @valid_from, @valid_until, @base_per_query, @per_row, @per_mib,
                        @per_compute_second, @per_analysis_cost, @source_cost_factor, @source_cost_unit, @unit_label, @compute_basis, @created_by, @created_at, @reason)";
                    insCmd.AddBound("@tenant_id", tariff.TenantId);
                    insCmd.AddBound("@data_source", tariff.DataSource);
                    insCmd.AddBound("@version", next, DbType.Int32);
                    AddDate(insCmd, "@valid_from", tariff.ValidFrom);
                    AddNullableDate(insCmd, "@valid_until", tariff.ValidUntil);
                    AddDecimal(insCmd, "@base_per_query", tariff.BasePerQuery);
                    AddDecimal(insCmd, "@per_row", tariff.PerRow);
                    AddDecimal(insCmd, "@per_mib", tariff.PerMib);
                    AddDecimal(insCmd, "@per_compute_second", tariff.PerComputeSecond);
                    AddDecimal(insCmd, "@per_analysis_cost", tariff.PerAnalysisCost);
                    AddDecimal(insCmd, "@source_cost_factor", tariff.SourceCostFactor);
                    insCmd.AddBound("@source_cost_unit", (object?)tariff.SourceCostUnit ?? DBNull.Value);
                    insCmd.AddBound("@unit_label", tariff.UnitLabel);
                    insCmd.AddBound("@compute_basis", tariff.ComputeBasis, DbType.Int16);
                    insCmd.AddBound("@created_by", tariff.CreatedBy);
                    AddDto(insCmd, "@created_at", createdAt);
                    insCmd.AddBound("@reason", tariff.Reason);
                    await repo.ExecuteNonQueryAsync(insCmd, "Usage.TariffInsert", ct).ConfigureAwait(false);
                }

                await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
                return tariff with { Version = next, CreatedAt = createdAt };
            }
            catch (DbException ex) when (attempt < 4 && (repo.Dialect.IsUniqueViolation(ex) || repo.Dialect.IsTransient(ex)))
            {
                await repo.RollbackTransactionAsync(tx, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await repo.RollbackTransactionAsync(tx, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException("Failed to add tariff version due to repeated concurrent conflicts.");
    }

    public static async Task<UsageGrantRefRecord?> GetGrantRefAsync(GovernanceRepository repo, TenantId tenant, string grantId, CancellationToken ct)
    {
        TenantScope.RequireTenant(tenant, "USAGE_GRANT_REF");
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT valid_from, valid_until, first_seen, last_seen FROM USAGE_GRANT_REF WHERE tenant_id = @tenant_id AND grant_id = @grant_id";
        cmd.AddBound("@tenant_id", tenant.Value);
        cmd.AddBound("@grant_id", grantId);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Usage.GetGrantRef", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)
            ? new UsageGrantRefRecord(tenant, grantId, ReadDto(r, 0), ReadDto(r, 1), ReadDate(r, 2), ReadDate(r, 3))
            : null;
    }

    public static async Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        GovernanceRepository repo, TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        TenantScope.RequireTenant(tenantId, "USAGE_DAILY");
        var fromDay = DateOnly.FromDateTime(fromUtc.UtcDateTime);
        var toDay = DateOnly.FromDateTime(toUtc.UtcDateTime);
        string tableKey = tableId.ToString();

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        var qNames = new Dictionary<(string Day, string App), List<string>>();
        using (var qCmd = conn.CreateCommand())
        {
            qCmd.CommandText = "SELECT day, application, query_name FROM CONSUMER_APP_QUERYNAME_DAILY WHERE tenant_id = @tenant_id AND table_id = @table_id AND day >= @f AND day <= @t ORDER BY query_name";
            qCmd.AddBound("@tenant_id", tenantId.Value);
            qCmd.AddBound("@table_id", tableKey);
            qCmd.AddBound("@f", fromDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            qCmd.AddBound("@t", toDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await using var qr = await repo.ExecuteReaderAsync(qCmd, "Usage.ConsumerQueryNames", ct).ConfigureAwait(false);
            while (await qr.ReadAsync(ct).ConfigureAwait(false))
            {
                var k = (qr.GetString(0), qr.GetString(1));
                if (!qNames.TryGetValue(k, out var l)) qNames[k] = l = new List<string>();
                l.Add(qr.GetString(2));
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT application, client_id, day, SUM(queries), SUM(rows_returned), SUM(rows_measured),
            SUM(deny_access), SUM(deny_rate_limit), SUM(deny_cost), SUM(deny_quota + deny_other)
            FROM USAGE_DAILY WHERE tenant_id = @tenant_id AND table_id = @table_id AND day >= @from_day AND day <= @to_day
            GROUP BY application, client_id, day ORDER BY day DESC, application ASC";
        cmd.AddBound("@tenant_id", tenantId.Value);
        cmd.AddBound("@table_id", tableKey);
        AddDate(cmd, "@from_day", fromDay);
        AddDate(cmd, "@to_day", toDay);

        var items = new List<ConsumerAppDailySummary>();
        await using var r = await repo.ExecuteReaderAsync(cmd, "Usage.ConsumerDaily", ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string app = r.GetString(0);
            string clientId = r.GetString(1);
            string dayStr = ReadDate(r, 2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            qNames.TryGetValue((dayStr, app), out var names);
            items.Add(new ConsumerAppDailySummary(app, dayStr, r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                new RejectionBreakdown(r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9)),
                Array.Empty<string>(), names ?? (IReadOnlyList<string>)Array.Empty<string>())
            { ClientId = string.IsNullOrEmpty(clientId) ? null : clientId });
        }

        return items;
    }

    public static async Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        GovernanceRepository repo, TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        TenantScope.RequireTenant(tenantId, "USAGE_DAILY");
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT application, COUNT(DISTINCT user_key) FROM USAGE_DAILY WHERE tenant_id = @tenant_id AND table_id = @table_id AND day >= @f AND day <= @t GROUP BY application";
        cmd.AddBound("@tenant_id", tenantId.Value);
        cmd.AddBound("@table_id", tableId.ToString());
        AddDate(cmd, "@f", DateOnly.FromDateTime(fromUtc.UtcDateTime));
        AddDate(cmd, "@t", DateOnly.FromDateTime(toUtc.UtcDateTime));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Usage.DistinctActors", ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) counts[r.GetString(0)] = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
        return counts;
    }

    private static async Task UpsertGroupSetsAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, UsageGenerationSnapshot snapshot, DateOnly utcToday, CancellationToken ct)
    {
        if (snapshot.GroupSets.Count == 0) return;
        var earliest = new Dictionary<(TenantId, string), DateOnly>();
        foreach (var k in snapshot.Series.Keys)
            if (!earliest.TryGetValue((k.Tenant, k.GroupSetId), out var c) || k.DayUtc < c) earliest[(k.Tenant, k.GroupSetId)] = k.DayUtc;

        var rows = new List<(TenantId T, string GsId, string Sid, DateOnly Day)>();
        foreach (var e in snapshot.GroupSets)
        {
            var d = earliest.TryGetValue(e.Key, out var first) ? first : utcToday;
            foreach (var s in e.Value) rows.Add((e.Key.Tenant, e.Key.GroupSetId, s.Value, d));
        }
        if (rows.Count == 0) return;

        int chunk = repo.Dialect.ParameterBudget.CalculateEffectiveChunkSize(200, 4);
        string sql = repo.Dialect.UpsertAccumulate(GovernanceTable.UsageGroupSet, GroupSetKeys, GroupSetCols, ReadOnlySpan<string>.Empty);
        for (int i = 0; i < rows.Count; i += chunk)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            for (int j = i; j < Math.Min(i + chunk, rows.Count); j++)
            {
                cmd.Parameters.Clear();
                cmd.AddBound("@tenant_id", rows[j].T.Value);
                cmd.AddBound("@group_set_id", rows[j].GsId);
                cmd.AddBound("@group_sid", rows[j].Sid);
                AddDate(cmd, "@first_seen", rows[j].Day);
                await repo.ExecuteNonQueryAsync(cmd, "Usage.UpsertGroupSet", ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task UpsertGrantRefsAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, UsageGenerationSnapshot snapshot, DateOnly utcToday, CancellationToken ct)
    {
        if (snapshot.GrantRefs.Count == 0) return;
        var ranges = new Dictionary<(TenantId, string), (DateOnly First, DateOnly Last)>();
        foreach (var k in snapshot.GrantSeries.Keys)
            ranges[(k.Tenant, k.GrantId)] = ranges.TryGetValue((k.Tenant, k.GrantId), out var c)
                ? (k.DayUtc < c.First ? k.DayUtc : c.First, k.DayUtc > c.Last ? k.DayUtc : c.Last) : (k.DayUtc, k.DayUtc);

        string insSql = repo.Dialect.UpsertAccumulate(GovernanceTable.UsageGrantRef, GrantRefKeys, GrantRefCols, ReadOnlySpan<string>.Empty);
        foreach (var entry in snapshot.GrantRefs)
        {
            var (first, last) = ranges.TryGetValue(entry.Key, out var r) ? r : (utcToday, utcToday);
            DateTimeOffset? inFrom = entry.Value.ValidFrom?.ToUniversalTime(), inUntil = entry.Value.ValidUntil?.ToUniversalTime();
            bool exists = false;
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT valid_from, valid_until FROM USAGE_GRANT_REF WHERE tenant_id = @tenant_id AND grant_id = @grant_id";
                sel.AddBound("@tenant_id", entry.Key.Tenant.Value);
                sel.AddBound("@grant_id", entry.Key.GrantId);
                await using var rd = await repo.ExecuteReaderAsync(sel, "Usage.SelectGrantRef", ct).ConfigureAwait(false);
                if (await rd.ReadAsync(ct).ConfigureAwait(false))
                {
                    exists = true;
                    if (ReadDto(rd, 0) != inFrom || ReadDto(rd, 1) != inUntil) UsageDiagnostics.GrantValidityConflictCounter.Add(1);
                }
            }

            if (!exists)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = insSql;
                ins.AddBound("@tenant_id", entry.Key.Tenant.Value);
                ins.AddBound("@grant_id", entry.Key.GrantId);
                AddDto(ins, "@valid_from", inFrom);
                AddDto(ins, "@valid_until", inUntil);
                AddDate(ins, "@first_seen", first);
                AddDate(ins, "@last_seen", last);
                await repo.ExecuteNonQueryAsync(ins, "Usage.InsertGrantRef", ct).ConfigureAwait(false);
            }

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE USAGE_GRANT_REF SET last_seen = @last_seen WHERE tenant_id = @tenant_id AND grant_id = @grant_id AND last_seen < @last_seen";
            AddDate(upd, "@last_seen", last);
            upd.AddBound("@tenant_id", entry.Key.Tenant.Value);
            upd.AddBound("@grant_id", entry.Key.GrantId);
            await repo.ExecuteNonQueryAsync(upd, "Usage.AdvanceGrantRef", ct).ConfigureAwait(false);
        }
    }

    private static async Task UpsertSeriesEntriesAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, IReadOnlyList<(UsageSeriesKey Key, UsageSeriesCounters Counters)> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        int chunk = repo.Dialect.ParameterBudget.CalculateEffectiveChunkSize(200, 29);
        string sql = repo.Dialect.UpsertAccumulate(GovernanceTable.UsageDaily, SeriesKeys, SeriesCols, SeriesCounters);
        for (int i = 0; i < entries.Count; i += chunk)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            for (int j = i; j < Math.Min(i + chunk, entries.Count); j++)
            {
                var (k, c) = entries[j];
                cmd.Parameters.Clear();
                AddDate(cmd, "@day", k.DayUtc);
                cmd.AddBound("@series_hash", ComputeSeriesHash(k), DbType.Binary);
                cmd.AddBound("@tenant_id", k.Tenant.Value);
                cmd.AddBound("@user_key", k.UserKey);
                cmd.AddBound("@group_set_id", k.GroupSetId);
                cmd.AddBound("@billing_group", k.BillingGroup);
                cmd.AddBound("@transport", (short)k.Transport, DbType.Int16);
                cmd.AddBound("@application", k.Application);
                cmd.AddBound("@client_id", k.ClientId);
                cmd.AddBound("@data_source", k.DataSource);
                cmd.AddBound("@table_id", k.TableId);
                cmd.AddBound("@requests", c.Requests, DbType.Int64); cmd.AddBound("@queries", c.Queries, DbType.Int64);
                cmd.AddBound("@rows_returned", c.Rows, DbType.Int64); cmd.AddBound("@rows_measured", c.RowsMeasured, DbType.Int64);
                cmd.AddBound("@bytes", c.Bytes, DbType.Int64); cmd.AddBound("@duration_ms", c.DurationMs, DbType.Int64);
                cmd.AddBound("@analysis_cost", c.AnalysisCost, DbType.Int64); cmd.AddBound("@source_compute_ms", c.SourceComputeMs, DbType.Int64);
                cmd.AddBound("@source_compute_measured", c.SourceComputeMeasured, DbType.Int64); cmd.AddBound("@source_cost_micros", c.SourceCostMicros, DbType.Int64);
                cmd.AddBound("@source_cost_measured", c.SourceCostMeasured, DbType.Int64); cmd.AddBound("@allow_count", c.Allow, DbType.Int64);
                cmd.AddBound("@deny_access", c.DenyAccess, DbType.Int64); cmd.AddBound("@deny_rate_limit", c.DenyRateLimit, DbType.Int64);
                cmd.AddBound("@deny_cost", c.DenyCost, DbType.Int64); cmd.AddBound("@deny_quota", c.DenyQuota, DbType.Int64);
                cmd.AddBound("@deny_other", c.DenyOther, DbType.Int64); cmd.AddBound("@errors", c.Errors, DbType.Int64);
                await repo.ExecuteNonQueryAsync(cmd, "Usage.UpsertSeries", ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task UpsertGrantSeriesAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, IReadOnlyDictionary<UsageGrantSeriesKey, UsageGrantSeriesCounters> grantSeries, CancellationToken ct)
    {
        if (grantSeries.Count == 0) return;
        var entries = new List<(UsageGrantSeriesKey Key, UsageGrantSeriesCounters Counters)>(grantSeries.Count);
        foreach (var kv in grantSeries) entries.Add((kv.Key, kv.Value));
        int chunk = repo.Dialect.ParameterBudget.CalculateEffectiveChunkSize(200, 12);
        string sql = repo.Dialect.UpsertAccumulate(GovernanceTable.UsageGrantDaily, GrantKeys, ReadOnlySpan<string>.Empty, GrantCounters);
        for (int i = 0; i < entries.Count; i += chunk)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            for (int j = i; j < Math.Min(i + chunk, entries.Count); j++)
            {
                var (k, c) = entries[j];
                cmd.Parameters.Clear();
                AddDate(cmd, "@day", k.DayUtc);
                cmd.AddBound("@tenant_id", k.Tenant.Value);
                cmd.AddBound("@grant_id", k.GrantId);
                cmd.AddBound("@data_source", k.DataSource);
                cmd.AddBound("@table_id", k.TableId);
                cmd.AddBound("@accesses", c.Accesses, DbType.Int64); cmd.AddBound("@queries", c.Queries, DbType.Int64);
                cmd.AddBound("@rows_returned", c.Rows, DbType.Int64); cmd.AddBound("@bytes", c.Bytes, DbType.Int64);
                cmd.AddBound("@analysis_cost", c.AnalysisCost, DbType.Int64); cmd.AddBound("@source_compute_ms", c.SourceComputeMs, DbType.Int64);
                cmd.AddBound("@source_cost_micros", c.SourceCostMicros, DbType.Int64);
                await repo.ExecuteNonQueryAsync(cmd, "Usage.UpsertGrantSeries", ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> PurgeTableAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string ssTable, string pgTable, DateOnly cutoff, int batchSize, CancellationToken ct)
    {
        int total = 0;
        while (true)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = repo.Dialect is PostgreSqlGovernanceDialect
                ? $"DELETE FROM {pgTable} WHERE ctid IN (SELECT ctid FROM {pgTable} WHERE day < @cutoff LIMIT @limit)"
                : $"DELETE TOP (@limit) FROM dbo.{ssTable} WHERE day < @cutoff";
            AddDate(cmd, "@cutoff", cutoff);
            cmd.AddBound("@limit", batchSize, DbType.Int32);
            int affected = await repo.ExecuteNonQueryAsync(cmd, "Usage.PurgeBatch", ct).ConfigureAwait(false);
            total += affected;
            if (affected < batchSize) break;
        }
        return total;
    }

    private static void AppendFilters(DbCommand cmd, StringBuilder w, UsageReportQuery q, string a, bool isGrant)
    {
        if (q.DataSource != null) { w.Append($" AND {a}.data_source = @ds"); cmd.AddBound("@ds", q.DataSource); }
        if (q.TableId != null) { w.Append($" AND {a}.table_id = @tbl"); cmd.AddBound("@tbl", q.TableId); }
        if (q.RestrictedTableIds is { Count: > 0 })
        {
            w.Append($" AND {a}.table_id IN (");
            for (int i = 0; i < q.RestrictedTableIds.Count; i++)
            {
                if (i > 0) w.Append(", ");
                string p = "@rt" + i.ToString(CultureInfo.InvariantCulture);
                w.Append(p);
                cmd.AddBound(p, q.RestrictedTableIds[i]);
            }
            w.Append(')');
        }
        if (isGrant) { if (q.GrantId != null) { w.Append(" AND gd.grant_id = @gid"); cmd.AddBound("@gid", q.GrantId); } return; }
        if (q.UserKey != null) { w.Append(" AND d.user_key = @uk"); cmd.AddBound("@uk", q.UserKey); }
        if (q.BillingGroup != null) { w.Append(" AND d.billing_group = @bg"); cmd.AddBound("@bg", q.BillingGroup); }
        if (q.Transport.HasValue) { w.Append(" AND d.transport = @tr"); cmd.AddBound("@tr", (short)q.Transport.Value, DbType.Int16); }
        if (q.Application != null) { w.Append(" AND d.application = @app"); cmd.AddBound("@app", q.Application); }
        if (q.ClientId != null) { w.Append(" AND d.client_id = @cid"); cmd.AddBound("@cid", q.ClientId); }
        if (q.GroupSid != null)
        {
            w.Append(q.GroupBy == UsageGroupBy.Group ? " AND gs.group_sid = @gsid" : " AND EXISTS (SELECT 1 FROM USAGE_GROUP_SET gs2 WHERE gs2.tenant_id = d.tenant_id AND gs2.group_set_id = d.group_set_id AND gs2.group_sid = @gsid)");
            cmd.AddBound("@gsid", q.GroupSid);
        }
    }

    private static void AppendCursor(DbCommand cmd, StringBuilder w, UsageGroupBy groupBy, string dimCol, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return;
        w.Append(" AND ").Append(dimCol).Append(" > @cursor");
        if (groupBy == UsageGroupBy.Day)
        {
            if (!DateOnly.TryParseExact(cursor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new ArgumentException("Invalid cursor.", nameof(cursor));
            AddDate(cmd, "@cursor", d);
        }
        else if (groupBy == UsageGroupBy.Transport)
        {
            if (!short.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) throw new ArgumentException("Invalid cursor.", nameof(cursor));
            cmd.AddBound("@cursor", t, DbType.Int16);
        }
        else cmd.AddBound("@cursor", cursor);
    }

    private static UsageReportRow ReadReportRow(DbDataReader r, UsageGroupBy gb) =>
        BuildReportRow(r, gb, null, null, 0, 1);

    private static UsageReportRow ReadBreakdownReportRow(DbDataReader r, UsageGroupBy gb) =>
        BuildReportRow(r, gb, ReadDate(r, 0), r.GetString(1), 2, 3);

    private static UsageReportRow BuildReportRow(DbDataReader r, UsageGroupBy gb, DateOnly? dayOverride, string? dataSourceOverride, int dimIdx, int c)
    {
        DateOnly? day = dayOverride ?? (gb == UsageGroupBy.Day ? ReadDate(r, dimIdx) : null);
        DataSourceTransportFlags? tr = gb == UsageGroupBy.Transport ? (DataSourceTransportFlags)Convert.ToInt16(r.GetValue(dimIdx), CultureInfo.InvariantCulture) : null;
        string? s = gb is UsageGroupBy.Day or UsageGroupBy.Transport ? null : r.GetString(dimIdx);
        return new UsageReportRow(
            day,
            gb == UsageGroupBy.User ? s : null,
            gb == UsageGroupBy.Group ? s : null,
            gb == UsageGroupBy.BillingGroup ? s : null,
            gb == UsageGroupBy.DataSource ? s : dataSourceOverride,
            gb == UsageGroupBy.Table ? s : null,
            tr,
            gb == UsageGroupBy.Application ? s : null,
            gb == UsageGroupBy.ClientId ? s : null,
            gb == UsageGroupBy.Grant ? s : null,
            r.GetInt64(c), r.GetInt64(c + 1), r.GetInt64(c + 2), r.GetInt64(c + 3), r.GetInt64(c + 4), r.GetInt64(c + 5),
            r.GetInt64(c + 6), r.GetInt64(c + 7), r.GetInt64(c + 8), r.GetInt64(c + 9), r.GetInt64(c + 10), r.GetInt64(c + 11),
            r.GetInt64(c + 12), r.GetInt64(c + 13), r.GetInt64(c + 14), r.GetInt64(c + 15), r.GetInt64(c + 16), r.GetInt64(c + 17));
    }

    private static string? EncodeCursor(UsageReportRow row, UsageGroupBy gb) => gb switch
    {
        UsageGroupBy.Day => row.DayUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        UsageGroupBy.User => row.UserKey, UsageGroupBy.Group => row.GroupSid, UsageGroupBy.BillingGroup => row.BillingGroup,
        UsageGroupBy.DataSource => row.DataSource, UsageGroupBy.Table => row.TableId,
        UsageGroupBy.Transport => row.Transport.HasValue ? ((short)row.Transport.Value).ToString(CultureInfo.InvariantCulture) : null,
        UsageGroupBy.Application => row.Application, UsageGroupBy.ClientId => row.ClientId, UsageGroupBy.Grant => row.GrantId, _ => null
    };

    private static byte[] ComputeSeriesHash(UsageSeriesKey k) => UsageSeriesHash.Compute(k);

    private static void AddDate(DbCommand c, string n, DateOnly v) => c.AddBound(n, new DateTime(v.Year, v.Month, v.Day, 0, 0, 0, DateTimeKind.Utc), DbType.Date);
    private static void AddNullableDate(DbCommand c, string n, DateOnly? v) => c.AddBound(n, v.HasValue ? new DateTime(v.Value.Year, v.Value.Month, v.Value.Day, 0, 0, 0, DateTimeKind.Utc) : DBNull.Value, DbType.Date);
    private static void AddDto(DbCommand c, string n, DateTimeOffset? v) => c.AddBound(n, v.HasValue ? v.Value.ToUniversalTime() : DBNull.Value, DbType.DateTimeOffset);

    private static void AddDecimal(DbCommand cmd, string name, decimal val)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name; p.DbType = DbType.Decimal; p.Precision = 19; p.Scale = 9; p.Value = val;
        cmd.Parameters.Add(p);
    }

    private static DateOnly ReadDate(DbDataReader r, int o) => r.GetValue(o) switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        var v => DateOnly.FromDateTime(Convert.ToDateTime(v, CultureInfo.InvariantCulture))
    };

    private static DateTimeOffset? ReadDto(DbDataReader r, int o) => r.IsDBNull(o) ? null : r.GetValue(o) switch
    {
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
        var v => throw new InvalidOperationException($"Unexpected timestamp type {v.GetType().Name}.")
    };
}
