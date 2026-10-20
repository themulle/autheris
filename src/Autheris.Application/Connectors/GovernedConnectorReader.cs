using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

/// <summary>
/// Architecture 2: how rows leave a connector. <paramref name="MaxRows"/> is checked while reading (before rows are
/// kept), <paramref name="MaxBytes"/> after projection. <paramref name="MaskingDisabled"/> is the Development-only
/// DANGER switch. <paramref name="VirtualFilterEnforcement"/> (F-GOV-14, C-6) is the gateway semi-join for the
/// table's applied Virtual Filters; it is passed here explicitly and never through <c>session.Items</c>.
/// <paramref name="SkipUpstream"/> (WI-6, Tier 0) skips only the connector call: all guards and the coverage check still
/// run, and it requires an enforcement (otherwise the read is denied).
/// </summary>
public sealed record GovernedRowPolicy(
    IColumnMaskingProvider MaskingProvider,
    string? HmacKeyId,
    bool MaskingDisabled = false,
    int? MaxRows = null,
    long? MaxBytes = null,
    VirtualFilterEnforcement? VirtualFilterEnforcement = null,
    bool SkipUpstream = false,
    IReadOnlyCollection<string>? RequestedColumns = null);

/// <summary>
/// Rows after governance. <paramref name="RowsBeforeVirtualFilter"/> is the number of upstream rows the Virtual
/// Filter enforcement received (null without enforcement).
/// </summary>
public sealed record GovernedReadResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool RlsPushdownExecuted,
    bool InDbMaskingExecuted,
    int? RowsBeforeVirtualFilter = null);

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
/// <item>enforce the Virtual Filters on the raw rows when an enforcement is passed (F-GOV-14); only then is the Virtual
/// Filter predicate removed from the row filter, after the coverage check and before the connector is called;</item>
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
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);

        // F-GOV-14 (M3, C-6): the enforcement and the decision are fixed before the connector runs; nothing the connector
        // writes into the session can relax them. The residual decision proves that the enforcement covers every applied
        // (filter, binding) pair, otherwise the read is denied without an upstream call.
        var enforcement = policy.VirtualFilterEnforcement;
        var decision = session.AccessDecision;
        if (enforcement != null)
        {
            if (enforcement.Table != metadata.Identifier)
            {
                throw new GatewaySecurityException(
                    $"The Virtual Filter enforcement for '{enforcement.Table}' cannot be applied to '{metadata.Identifier}'.",
                    VirtualFilterErrorCodes.EnforcementIncomplete);
            }

            decision = decision.WithoutEnforcedVirtualFilters(enforcement.Bindings);
        }

        if (policy.SkipUpstream && enforcement == null)
        {
            throw new GatewaySecurityException(
                $"The upstream read of '{metadata.Identifier}' can only be skipped together with a Virtual Filter enforcement.",
                VirtualFilterErrorCodes.EnforcementIncomplete);
        }

        // SEC-2 (F-GOV-14): an HTTP API cannot evaluate the gateway's row filter or masking rules, so a declarative HTTP
        // connector never relaxes enforcement by self-attestation; the gateway always filters and masks its rows.
        var mayAttest = metadata.Table?.DataSourceType != DataSourceType.HttpDeclarative;

        // CR-10 (D-12): a table that cannot attest and carries a Virtual Filter predicate without an enforcement (OData, REST
        // and the other channels, which do not federate) is denied before the upstream is read: no egress for nothing and
        // no unauthorized row in gateway memory. After the read the same check would deny, too late.
        if (!mayAttest && !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            GatewayExecutionService.EnsureInMemoryFilterIsEnforceable(decision);
        }

        var rawRows = policy.SkipUpstream
            ? new List<IReadOnlyDictionary<string, object?>>()
            : await ReadRawAsync(connector, session, metadata, policy.MaxRows, ct).ConfigureAwait(false);
        int? rowsBeforeVirtualFilter = null;
        if (enforcement != null)
        {
            rowsBeforeVirtualFilter = rawRows.Count;
            rawRows = enforcement.Apply(rawRows);
        }

        var pushedDown = mayAttest && IsSet(session.Items, RlsPushdownExecutedKey);
        var masked = mayAttest && IsSet(session.Items, InDbColumnMaskingExecutedKey);
        var rows = Apply(rawRows, metadata, decision, session.Tenant?.Value, pushedDown, masked, policy);
        return new GovernedReadResult(rows, pushedDown, masked, rowsBeforeVirtualFilter);
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
    /// Reads splits and yields raw rows one-by-one via IAsyncEnumerable without buffering into a list.
    /// Evaluates MaxRows on-the-fly.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawAsync(
        IAutherisConnector connector,
        ConnectorSessionContext session,
        TableMetadata metadata,
        int? maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);

        session.Items["TableMetadata"] = metadata;
        var splits = await connector.SplitManager.GetSplitsAsync(metadata, session, ct).ConfigureAwait(false);
        int totalCount = 0;

        foreach (var split in splits)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await connector.RecordSource.ReadBatchAsync(split, session, ct).ConfigureAwait(false);
            foreach (var row in batch)
            {
                if (maxRows is { } max && ++totalCount > max)
                {
                    throw new ConnectorRowLimitExceededException(metadata.Identifier, max);
                }
                yield return row;
            }
        }
    }

    /// <summary>
    /// Governed row-by-row streaming: streams from upstream, evaluates Virtual Filter, row filter,
    /// column masking/projection, and cumulative byte limit row-by-row without buffering in memory.
    /// SEC-PERF254-03: Throws RESPONSE_TOO_LARGE as soon as cumulativeEstimatedBytes exceeds policy.MaxBytes.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamAsync(
        IAutherisConnector connector,
        ConnectorSessionContext session,
        TableMetadata metadata,
        TableAccessDecision decision,
        GovernedRowPolicy policy,
        VirtualFilterEnforcement? enforcement = null,
        bool mayAttest = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.SkipUpstream)
        {
            yield break;
        }

        var effectiveDecision = decision;
        if (enforcement != null)
        {
            if (enforcement.Table != metadata.Identifier)
            {
                throw new GatewaySecurityException(
                    $"The Virtual Filter enforcement for '{enforcement.Table}' cannot be applied to '{metadata.Identifier}'.",
                    VirtualFilterErrorCodes.EnforcementIncomplete);
            }

            effectiveDecision = effectiveDecision.WithoutEnforcedVirtualFilters(enforcement.Bindings);
        }

        if (!mayAttest && !string.IsNullOrWhiteSpace(effectiveDecision.CombinedRowFilterSql))
        {
            GatewayExecutionService.EnsureInMemoryFilterIsEnforceable(effectiveDecision);
        }

        var pushedDown = mayAttest && IsSet(session.Items, RlsPushdownExecutedKey);
        var masked = mayAttest && IsSet(session.Items, InDbColumnMaskingExecutedKey);
        long cumulativeEstimatedBytes = 0;

        await foreach (var rawRow in StreamRawAsync(connector, session, metadata, policy.MaxRows, ct).ConfigureAwait(false))
        {
            var row = rawRow;
            if (enforcement != null)
            {
                var singleList = new List<IReadOnlyDictionary<string, object?>>(1) { row };
                var filtered = enforcement.Apply(singleList);
                if (filtered.Count == 0) continue;
                row = filtered[0];
            }

            if (!pushedDown && !string.IsNullOrWhiteSpace(effectiveDecision.CombinedRowFilterSql))
            {
                var singleList = new List<IReadOnlyDictionary<string, object?>>(1) { row };
                var filtered = GatewayExecutionService.FilterRows(singleList, effectiveDecision, metadata);
                if (filtered.Count == 0) continue;
                row = filtered[0];
            }

            var projected = ProjectRow(row, metadata, effectiveDecision, session.Tenant?.Value, policy, masked);

            if (policy.MaxBytes is { } maxBytes)
            {
                cumulativeEstimatedBytes += EstimateRowBytes(projected);
                if (cumulativeEstimatedBytes > maxBytes)
                {
                    throw new GatewaySecurityException($"Response size ({cumulativeEstimatedBytes} bytes) exceeds the configured limit of {maxBytes} bytes.", "RESPONSE_TOO_LARGE");
                }
            }

            yield return projected;
        }
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
            filtered = GatewayExecutionService.FilterRows(filtered, decision, metadata);
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

        IEnumerable<TableColumn> targetColumns = metadata.Columns;
        if (policy.RequestedColumns != null && policy.RequestedColumns.Count > 0)
        {
            var requestedSet = policy.RequestedColumns is HashSet<string> set
                ? set
                : new HashSet<string>(policy.RequestedColumns, StringComparer.OrdinalIgnoreCase);
            targetColumns = metadata.Columns.Where(c => requestedSet.Contains(c.ColumnName));
        }

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in targetColumns)
        {
            // Zero-Trust: sensitive columns in the catalog never leave in clear text without an explicit Clear rule (for a holder of a
            // restricted scope a scope-specific one, REQ-GOV-143). The decision owns that rule; no path-local copy of it.
            var access = decision.GetEffectiveColumnAccess(col.ColumnName, metadata);
            if (access == ColumnAccessLevel.Deny)
            {
                continue;
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

    public static long EstimateRowBytes(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        long estimatedBytes = 0;
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
        return estimatedBytes;
    }

    public static long EstimateBytes(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        long estimatedBytes = 0;
        foreach (var row in rows)
        {
            estimatedBytes += EstimateRowBytes(row);
        }

        return estimatedBytes;
    }

    private static bool IsSet(IDictionary<string, object?> items, string key) =>
        items.TryGetValue(key, out var value) && value is true;
}
