using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

/// <summary>
/// Architecture 2: how rows leave a connector. <paramref name="MaxRows"/> is checked while reading (before rows are
/// kept), <paramref name="MaxBytes"/> after projection. <paramref name="MaskingDisabled"/> is the Development-only
/// DANGER switch.
/// </summary>
public sealed record GovernedRowPolicy(
    IColumnMaskingProvider MaskingProvider,
    string? HmacKeyId,
    bool MaskingDisabled = false,
    int? MaxRows = null,
    long? MaxBytes = null);

public sealed record GovernedReadResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool RlsPushdownExecuted,
    bool InDbMaskingExecuted);

public sealed class ConnectorRowLimitExceededException : InvalidOperationException
{
    public ConnectorRowLimitExceededException(TableIdentifier table, int maxRows)
        : base($"Table '{table}' exceeds the maximum of {maxRows} rows.")
    {
        Table = table;
        MaxRows = maxRows;
    }

    public TableIdentifier Table { get; }

    public int MaxRows { get; }
}

/// <summary>
/// Architecture 2 / SQL2-5 / SQL2-4: the one governed read path for connectors. Every caller (REST, OData, GraphQL,
/// OLAP, connector adapters) gets the same steps in the same order:
/// <list type="number">
/// <item>read all splits, enforcing the row limit before rows are kept;</item>
/// <item>apply the row filter in memory unless the connector reports that it pushed the filter down;</item>
/// <item>strip denied columns and mask exactly once (not again when the connector masked in the database);</item>
/// <item>enforce the byte limit.</item>
/// </list>
/// </summary>
public static class GovernedConnectorReader
{
    public const string RlsPushdownExecutedKey = "RlsPushdownExecuted";
    public const string InDbColumnMaskingExecutedKey = "InDbColumnMaskingExecuted";

    public static async Task<GovernedReadResult> ReadAsync(
        IAutherisConnector connector,
        ConnectorSessionContext session,
        TableMetadata metadata,
        GovernedRowPolicy policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rawRows = await ReadRawAsync(connector, session, metadata, policy.MaxRows, ct).ConfigureAwait(false);
        var pushedDown = IsSet(session.Items, RlsPushdownExecutedKey);
        var masked = IsSet(session.Items, InDbColumnMaskingExecutedKey);
        var rows = Apply(rawRows, metadata, session.AccessDecision, session.Tenant?.Value, pushedDown, masked, policy);
        return new GovernedReadResult(rows, pushedDown, masked);
    }

    /// <summary>Reads every split of the connector. Rows are not filtered or masked yet; see <see cref="Apply"/>.</summary>
    public static async Task<List<IReadOnlyDictionary<string, object?>>> ReadRawAsync(
        IAutherisConnector connector,
        ConnectorSessionContext session,
        TableMetadata metadata,
        int? maxRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);

        session.Items["TableMetadata"] = metadata;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var splits = await connector.SplitManager.GetSplitsAsync(metadata, session, ct).ConfigureAwait(false);
        foreach (var split in splits)
        {
            var batch = await connector.RecordSource.ReadBatchAsync(split, session, ct).ConfigureAwait(false);
            if (maxRows is { } max && rows.Count + batch.Count > max)
            {
                throw new ConnectorRowLimitExceededException(metadata.Identifier, max);
            }

            rows.AddRange(batch);
        }

        return rows;
    }

    /// <summary>
    /// Row filter (unless pushed down), column projection with masking (unless masked in the database) and byte limit.
    /// Also used for rows from <see cref="IDataSourceExecutor"/>s, which report the same two flags.
    /// </summary>
    public static List<IReadOnlyDictionary<string, object?>> Apply(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rawRows,
        TableMetadata metadata,
        TableAccessDecision decision,
        string? tenantId,
        bool rlsPushdownExecuted,
        bool inDbMaskingExecuted,
        GovernedRowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(rawRows);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(policy);

        var filtered = rawRows as List<IReadOnlyDictionary<string, object?>> ?? new List<IReadOnlyDictionary<string, object?>>(rawRows);
        if (!rlsPushdownExecuted && !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            GatewayExecutionService.EnsureInMemoryFilterIsEnforceable(decision);
            filtered = GatewayExecutionService.FilterRows(filtered, decision.CombinedRowFilterSql, metadata);
        }

        var projected = new List<IReadOnlyDictionary<string, object?>>(filtered.Count);
        foreach (var row in filtered)
        {
            projected.Add(ProjectRow(row, metadata, decision, tenantId, policy, inDbMaskingExecuted));
        }

        if (policy.MaxBytes is { } maxBytes)
        {
            var estimatedBytes = EstimateBytes(projected);
            if (estimatedBytes > maxBytes)
            {
                throw new GatewaySecurityException($"Response size ({estimatedBytes} bytes) exceeds the configured limit of {maxBytes} bytes.", "RESPONSE_TOO_LARGE");
            }
        }

        return projected;
    }

    /// <summary>
    /// Denied columns are stripped; catalog-sensitive columns are masked unless an explicit Clear rule exists; a column
    /// the source did not return is null. HMAC rules are tenant-scoped (SEC D-3).
    /// </summary>
    public static Dictionary<string, object?> ProjectRow(
        IReadOnlyDictionary<string, object?> rawRow,
        TableMetadata metadata,
        TableAccessDecision decision,
        string? tenantId,
        GovernedRowPolicy policy,
        bool alreadyMasked)
    {
        ArgumentNullException.ThrowIfNull(rawRow);
        ArgumentNullException.ThrowIfNull(policy);

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var access = decision.GetColumnAccess(col.ColumnName);
            if (access == ColumnAccessLevel.Deny)
            {
                continue;
            }

            // Zero-Trust: sensitive columns in the catalog never leave in clear text without an explicit Clear rule.
            bool isSensitiveInCatalog = col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName);
            if (isSensitiveInCatalog && !decision.HasExplicitClear(col.ColumnName))
            {
                access = ColumnAccessLevel.Mask;
            }

            if (!rawRow.TryGetValue(col.ColumnName, out var value))
            {
                dict[col.ColumnName] = null;
                continue;
            }

            if (access == ColumnAccessLevel.Mask && !alreadyMasked && !policy.MaskingDisabled)
            {
                var rule = metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                    ? mRule
                    : new MaskingRule { RuleType = "REDACT" };
                rule = GatewayExecutionService.ScopeRuleForTenant(rule, tenantId, policy.HmacKeyId);
                value = policy.MaskingProvider.MaskValue(col.ColumnName, value, rule);
            }

            dict[col.ColumnName] = value;
        }

        return dict;
    }

    public static long EstimateBytes(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        long estimatedBytes = 0;
        foreach (var row in rows)
        {
            foreach (var (key, val) in row)
            {
                estimatedBytes += key.Length * 2;
                estimatedBytes += val switch
                {
                    string s => s.Length * 2,
                    byte[] b => b.Length,
                    null => 0,
                    _ => 16
                };
            }
        }

        return estimatedBytes;
    }

    private static bool IsSet(IDictionary<string, object?> items, string key) =>
        items.TryGetValue(key, out var value) && value is true;
}
