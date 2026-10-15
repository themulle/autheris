namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Data;
using Autheris.Application.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed partial class SemanticMetricQueryService
{
    private async Task<(TableAccessDecision Decision, bool HasMetricConsent)> ResolveMetricAndTableDecisionAsync(
        ClaimsPrincipal principal,
        Sid userSid,
        TenantId tenantId,
        TableMetadata tableMetadata,
        IReadOnlyList<DbtMetric> metrics,
        CancellationToken ct)
    {
        var table = tableMetadata.Identifier;
        var allSids = principal.GetAllUserSids();
        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();
        var subjects = (allSids.Count > 0 ? allSids : (IEnumerable<Sid>)[userSid])
            .Concat(groupSids)
            .Distinct()
            .ToList();

        // 1. Check standard table-level decision first
        TableAccessDecision? tableDecision = null;
        if (_pdp != null)
        {
            var secCtx = SecurityPrincipalContext.FromPrincipal(principal, tenantId);
            tableDecision = await _pdp.EvaluateAccessAsync(table, tableMetadata, secCtx, requestedColumns: null, ct).ConfigureAwait(false);
        }
        else if (_accessResolver != null)
        {
            var resolved = await _accessResolver.ResolveTableAccessAsync(principal, table, null, null, ct).ConfigureAwait(false);
            tableDecision = resolved.Decision;
        }
        else if (_consentRepository != null && _resolutionService != null)
        {
            var tableConsents = (await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false))
                .Where(c => c.TenantId == tenantId)
                .ToList();
            if (tableConsents.Count > 0)
            {
                var scopes = TokenAccessScope.GetScopeSet(principal.Claims);
                tableDecision = _resolutionService.ResolveAccess(
                    userSid,
                    groupSids,
                    roles,
                    table,
                    tableConsents,
                    tableMetadata.Dialect,
                    allSids.Count > 1 ? allSids : null,
                    scopes.Count > 0 ? scopes : null);
            }
        }

        if (tableDecision is { IsAllowed: true })
        {
            return (tableDecision, false);
        }

        // If table access was denied by an explicit DENY consent, fail closed immediately.
        if (tableDecision != null && tableDecision.DeniedReasons.Any(r => r.Contains("Explicit Deny", StringComparison.OrdinalIgnoreCase)))
        {
            return (tableDecision, false);
        }

        // 2. Check metric-scoped consents (e.g. "metric:<metricName>") for every requested metric
        if (_consentRepository != null)
        {
            var now = DateTimeOffset.UtcNow;
            var allSubjectConsents = (await _consentRepository.GetAllActiveConsentsForSubjectsAsync(subjects, roles, now, tenantId, ct).ConfigureAwait(false))
                .Where(c => c.TenantId == tenantId && c.IsActive(now))
                .ToList();

            var matchedMetricConsents = new List<Consent>();
            bool allMetricsPermitted = true;

            foreach (var metric in metrics)
            {
                var metricConsents = new List<Consent>();
                foreach (var c in allSubjectConsents)
                {
                    if (MatchesMetricIdentifier(c.TableIdentifier, metric.Name))
                    {
                        metricConsents.Add(c);
                    }
                }

                // Also probe GetActiveConsentsForSubjectsAsync for standard metric table identifiers
                foreach (var candidateId in CandidateMetricIdentifiers(table, metric.Name))
                {
                    var specific = await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, candidateId, now, tenantId, ct).ConfigureAwait(false);
                    foreach (var c in specific.Where(c => c.TenantId == tenantId && c.IsActive(now)))
                    {
                        if (metricConsents.All(existing => existing.Id != c.Id))
                        {
                            metricConsents.Add(c);
                        }
                    }
                }

                if (metricConsents.Any(c => c.Effect == ConsentEffect.Deny))
                {
                    return (TableAccessDecision.Denied(table, $"Explicit Deny consent for metric '{metric.Name}'."), false);
                }

                if (!metricConsents.Any(c => c.Effect == ConsentEffect.Allow))
                {
                    allMetricsPermitted = false;
                    break;
                }

                matchedMetricConsents.AddRange(metricConsents);
            }

            if (allMetricsPermitted && matchedMetricConsents.Count > 0)
            {
                // Project metric consents onto the base table so ConsentResolutionService computes their RLS row filters and column rules!
                var projectedConsents = matchedMetricConsents
                    .Select(c => new Consent
                    {
                        Id = c.Id,
                        TableId = tableMetadata.Table.Id,
                        TableIdentifier = table,
                        ConsentRequestId = c.ConsentRequestId,
                        Effect = c.Effect,
                        TenantId = c.TenantId,
                        GranteeType = c.GranteeType,
                        GranteeSid = c.GranteeSid,
                        RoleId = c.RoleId,
                        RoleName = c.RoleName,
                        ValidFrom = c.ValidFrom,
                        ValidTo = c.ValidTo,
                        IsRevoked = c.IsRevoked,
                        ColumnRules = c.ColumnRules,
                        RowFilters = c.RowFilters
                    })
                    .ToList();

                if (_resolutionService != null)
                {
                    var scopes = TokenAccessScope.GetScopeSet(principal.Claims);
                    var metricDecision = _resolutionService.ResolveAccess(
                        userSid,
                        groupSids,
                        roles,
                        table,
                        projectedConsents,
                        tableMetadata.Dialect,
                        allSids.Count > 1 ? allSids : null,
                        scopes.Count > 0 ? scopes : null);

                    if (metricDecision is { IsAllowed: true })
                    {
                        return (metricDecision, true);
                    }
                }

                // Fallback when no IConsentResolutionService was provided in unit test construction
                var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in tableMetadata.Columns)
                {
                    colAccess[col.ColumnName] = col.IsSensitive || tableMetadata.ColumnMaskingRules.ContainsKey(col.ColumnName)
                        ? ColumnAccessLevel.Mask
                        : ColumnAccessLevel.Clear;
                }

                foreach (var rule in projectedConsents.SelectMany(c => c.ColumnRules))
                {
                    colAccess[rule.ColumnName] = rule.AccessLevel;
                }

                return (TableAccessDecision.Allowed(table, colAccess, hasUnconstrainedColumnAllow: true), true);
            }
        }

        return (tableDecision ?? TableAccessDecision.Denied(table, "No active table or metric consent found."), false);
    }

    private static bool MatchesMetricIdentifier(TableIdentifier id, string metricName)
    {
        var metricToken = $"metric:{metricName}";
        if (string.Equals(id.TableName, metricToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.TableName, metricName, StringComparison.OrdinalIgnoreCase) && string.Equals(id.Domain, "metric", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.TableName, metricName, StringComparison.OrdinalIgnoreCase) && string.Equals(id.Schema, "metric", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.ToString(), metricToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.ToQualifiedName(), $"metric.{metricName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<TableIdentifier> CandidateMetricIdentifiers(TableIdentifier baseTable, string metricName)
    {
        yield return new TableIdentifier(baseTable.Domain, baseTable.Schema, $"metric:{metricName}");
        yield return new TableIdentifier("metric", "default", metricName);
        yield return new TableIdentifier("default", "metric", metricName);
        yield return new TableIdentifier("default", "default", $"metric:{metricName}");
    }

    private static AggregateFunction ParseAggregateFunction(string? agg) =>
        (agg ?? "sum").Trim().ToLowerInvariant() switch
        {
            "sum" => AggregateFunction.Sum,
            "count" => AggregateFunction.Count,
            "avg" or "average" => AggregateFunction.Avg,
            "min" => AggregateFunction.Min,
            "max" => AggregateFunction.Max,
            "count_distinct" or "countdistinct" => AggregateFunction.CountDistinct,
            _ => throw new ArgumentException($"Unsupported measure aggregation function '{agg}'.", nameof(agg))
        };

    private static string? ResolveMeasureColumn(DbtMeasure measure, AggregateFunction aggFunc, TableMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(measure.Expr))
        {
            var expr = measure.Expr.Trim();
            if (expr is "1" or "*")
            {
                return aggFunc == AggregateFunction.Count ? null : expr;
            }

            return expr;
        }

        if (metadata.HasColumn(measure.Name))
        {
            return measure.Name;
        }

        return aggFunc == AggregateFunction.Count ? null : measure.Name;
    }

    private static TableFilterClause? CombineMetricFiltersWithWhere(
        IEnumerable<DbtMetric> metrics,
        TableFilterClause? requestWhere,
        TableMetadata metadata)
    {
        var predicates = new List<string>();
        var referencedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (requestWhere != null && !string.IsNullOrWhiteSpace(requestWhere.SqlPredicate))
        {
            predicates.Add(requestWhere.SqlPredicate);
            foreach (var c in requestWhere.ReferencedColumns)
            {
                referencedCols.Add(c);
            }
            foreach (var (k, v) in requestWhere.Parameters)
            {
                parameters[k] = v;
            }
        }

        foreach (var metric in metrics)
        {
            if (!string.IsNullOrWhiteSpace(metric.Filter))
            {
                var cleaned = CleanDbtFilterTemplate(metric.Filter);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    predicates.Add(cleaned);
                    foreach (var col in Autheris.Application.Data.Services.DataApiFilterTranslator.ExtractReferencedColumns(cleaned, metadata))
                    {
                        referencedCols.Add(col);
                    }
                }
            }
        }

        if (predicates.Count == 0)
        {
            return null;
        }

        if (predicates.Count == 1 && requestWhere != null)
        {
            return requestWhere;
        }

        string combinedSql = predicates.Count == 1
            ? predicates[0]
            : string.Join(" AND ", predicates.Select(p => $"({p})"));

        return new TableFilterClause(combinedSql, parameters, referencedCols.ToList())
        {
            TrinoSqlFactory = _ => combinedSql
        };
    }

    private static string CleanDbtFilterTemplate(string rawFilter)
    {
        var text = rawFilter.Trim();
        if (text.StartsWith("{{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal))
        {
            text = text[2..^2].Trim();
        }

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?:Dimension|TimeDimension|Entity)\(\s*['""](?:[A-Za-z0-9_]+__)?([A-Za-z0-9_]+)['""]\s*\)",
            "$1");

        return text;
    }

    private static AggregationGroupRow EnrichGroupWithMetricValues(
        AggregationGroupRow group,
        IEnumerable<DbtMetric> metrics,
        IReadOnlyDictionary<string, DbtMeasure> measures)
    {
        var enrichedAggs = new Dictionary<string, object?>(group.Aggregates, StringComparer.OrdinalIgnoreCase);
        foreach (var metric in metrics)
        {
            if (enrichedAggs.ContainsKey(metric.Name))
            {
                continue;
            }

            if (IsSimpleOrCumulative(metric.Type) && metric.Measures.Count >= 1)
            {
                var mName = metric.Measures[0];
                if (enrichedAggs.TryGetValue(mName, out var val))
                {
                    enrichedAggs[metric.Name] = val;
                }
            }
            else if (string.Equals(metric.Type, "ratio", StringComparison.OrdinalIgnoreCase))
            {
                enrichedAggs[metric.Name] = EvaluateRatioMetric(metric, enrichedAggs);
            }
            else if (string.Equals(metric.Type, "derived", StringComparison.OrdinalIgnoreCase))
            {
                enrichedAggs[metric.Name] = EvaluateDerivedMetric(metric, enrichedAggs, measures.Keys);
            }
        }

        return group with { Aggregates = enrichedAggs };
    }

    private static object? EvaluateRatioMetric(DbtMetric metric, IReadOnlyDictionary<string, object?> aggValues)
    {
        if (metric.Measures.Count >= 2 &&
            aggValues.TryGetValue(metric.Measures[0], out var numObj) &&
            aggValues.TryGetValue(metric.Measures[1], out var denObj) &&
            numObj is not null && denObj is not null)
        {
            decimal num = Convert.ToDecimal(numObj, CultureInfo.InvariantCulture);
            decimal den = Convert.ToDecimal(denObj, CultureInfo.InvariantCulture);
            return den == 0m ? null : Math.Round(num / den, 6);
        }

        return null;
    }

    private static object? EvaluateDerivedMetric(
        DbtMetric metric,
        IReadOnlyDictionary<string, object?> aggValues,
        IEnumerable<string> measureNames)
    {
        if (string.IsNullOrWhiteSpace(metric.TypeParams))
        {
            if (metric.Measures.Count >= 2 &&
                aggValues.TryGetValue(metric.Measures[0], out var first) &&
                aggValues.TryGetValue(metric.Measures[1], out var second) &&
                first is not null && second is not null)
            {
                return Convert.ToDecimal(first, CultureInfo.InvariantCulture) - Convert.ToDecimal(second, CultureInfo.InvariantCulture);
            }
            return null;
        }

        var expr = metric.TypeParams;
        foreach (var mName in measureNames.OrderByDescending(s => s.Length))
        {
            if (aggValues.TryGetValue(mName, out var val) && val is not null)
            {
                var decStr = Convert.ToDecimal(val, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                expr = System.Text.RegularExpressions.Regex.Replace(
                    expr,
                    $@"\b{System.Text.RegularExpressions.Regex.Escape(mName)}\b",
                    decStr,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        try
        {
            using var table = new System.Data.DataTable();
            var computed = table.Compute(expr, null);
            return computed is null or DBNull ? null : Convert.ToDecimal(computed, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSimpleOrCumulative(string? type) =>
        string.IsNullOrWhiteSpace(type) ||
        string.Equals(type, "simple", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "cumulative", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "conversion", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIdentifier(string expr, string identifier) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            expr,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(identifier)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private Task RecordMetricAuditAsync(
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        bool allowed,
        IReadOnlyList<string> metrics,
        string manifestVersion,
        object details,
        CancellationToken ct)
    {
        if (_auditWriter == null)
        {
            return Task.CompletedTask;
        }

        var target = metrics.Count == 1
            ? $"metric:{metrics[0]} ({table})"
            : $"metrics:{string.Join(",", metrics)} ({table})";

        var entry = GovernedQueryStages.BuildEntry(
            AuditEventTypes.MetricQuery,
            tenantId,
            userSid,
            target,
            allowed,
            details);

        return GovernedQueryStages.WriteAsync(_auditWriter, entry, AuditTier.Required, ct);
    }
}
