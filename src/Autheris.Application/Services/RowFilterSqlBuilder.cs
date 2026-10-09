using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

public sealed partial class RowFilterSqlBuilder : IRowFilterSqlBuilder
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    private static readonly HashSet<string> AllowedFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "EQ", "NEQ", "LT", "GT", "LTE", "GTE", "LIKE", "IN"
    };

    private readonly IRlsFilterGenerator _rlsFilterGenerator;
    private readonly RowFilterSubqueryStrategy _subqueryStrategy;

    public RowFilterSqlBuilder(IRlsFilterGenerator? rlsFilterGenerator = null, IOptions<Autheris.Domain.Options.GatewayOptions>? options = null)
    {
        _rlsFilterGenerator = rlsFilterGenerator ?? RlsFilterGenerator.Instance;
        _subqueryStrategy = options?.Value.RowFilters?.SubqueryStrategy ?? RowFilterSubqueryStrategy.Exists;
    }

    public string? BuildCombinedRowFilter(
        IReadOnlyList<Consent> aConsents,
        IReadOnlyList<Consent> dConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        // Consents in A: combined with OR. If ANY allow consent has NO row filters, all rows are allowed.
        var hasUnconstrainedAllow = aConsents.Any(c => c.RowFilters.Count == 0);

        string? aSql = null;
        if (!hasUnconstrainedAllow)
        {
            var consentPredicates = new List<string>();
            foreach (var consent in aConsents)
            {
                var consentSql = BuildConsentFilterPredicate(consent.RowFilters, dialect, isDeny: false);
                if (!string.IsNullOrEmpty(consentSql))
                {
                    consentPredicates.Add(consentSql);
                }
            }

            if (consentPredicates.Count > 0)
            {
                aSql = consentPredicates.Count == 1
                    ? consentPredicates[0]
                    : $"({string.Join(" OR ", consentPredicates)})";
            }
        }

        // Filters in D: combined with AND NOT (...)
        // Each DENY consent represents an independent exclusion: NOT ((D1) OR (D2) ...)
        var denyPredicates = new List<string>();
        foreach (var denyConsent in dConsents)
        {
            if (denyConsent.RowFilters.Count == 0) continue;

            var dPredicate = BuildConsentFilterPredicate(denyConsent.RowFilters, dialect, isDeny: true);
            if (!string.IsNullOrEmpty(dPredicate))
            {
                denyPredicates.Add(dPredicate);
            }
        }

        string? dSql = null;
        if (denyPredicates.Count > 0)
        {
            var combinedDeny = denyPredicates.Count == 1
                ? denyPredicates[0]
                : $"({string.Join(" OR ", denyPredicates)})";
            dSql = $"NOT ({combinedDeny})";
        }

        if (aSql != null && dSql != null)
        {
            return $"({aSql} AND {dSql})";
        }
        if (aSql != null)
        {
            return aSql;
        }
        if (dSql != null)
        {
            return dSql;
        }

        return null;
    }

    public ParameterizedRowFilter BuildCombinedRowFilterParameterized(
        IReadOnlyList<Consent> aConsents,
        IReadOnlyList<Consent> dConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        int paramIndex = 0;

        var hasUnconstrainedAllow = aConsents.Any(c => c.RowFilters.Count == 0);

        string? aSql = null;
        if (!hasUnconstrainedAllow)
        {
            var consentPredicates = new List<string>();
            foreach (var consent in aConsents)
            {
                var consentSql = BuildConsentFilterPredicateParameterized(consent.RowFilters, dialect, parameters, ref paramIndex, isDeny: false);
                if (!string.IsNullOrEmpty(consentSql))
                {
                    consentPredicates.Add(consentSql);
                }
            }

            if (consentPredicates.Count > 0)
            {
                aSql = consentPredicates.Count == 1
                    ? consentPredicates[0]
                    : $"({string.Join(" OR ", consentPredicates)})";
            }
        }

        var denyPredicates = new List<string>();
        foreach (var denyConsent in dConsents)
        {
            if (denyConsent.RowFilters.Count == 0) continue;

            var dPredicate = BuildConsentFilterPredicateParameterized(denyConsent.RowFilters, dialect, parameters, ref paramIndex, isDeny: true);
            if (!string.IsNullOrEmpty(dPredicate))
            {
                denyPredicates.Add(dPredicate);
            }
        }

        string? dSql = null;
        if (denyPredicates.Count > 0)
        {
            var combinedDeny = denyPredicates.Count == 1
                ? denyPredicates[0]
                : $"({string.Join(" OR ", denyPredicates)})";
            dSql = $"NOT ({combinedDeny})";
        }

        string? finalSql = null;
        if (aSql != null && dSql != null)
        {
            finalSql = $"({aSql} AND {dSql})";
        }
        else if (aSql != null)
        {
            finalSql = aSql;
        }
        else if (dSql != null)
        {
            finalSql = dSql;
        }

        return new ParameterizedRowFilter(finalSql, parameters);
    }

    private string? BuildConsentFilterPredicateParameterized(
        IReadOnlyList<ConsentRowFilter> filters,
        DatabaseDialect dialect,
        Dictionary<string, object?> parameters,
        ref int paramIndex,
        bool isDeny = false)
    {
        if (filters.Count == 0) return null;

        var groups = filters.GroupBy(f => f.FilterGroup).ToList();
        var groupPredicates = new List<string>();

        foreach (var group in groups)
        {
            var groupConditions = new List<string>();
            foreach (var f in group)
            {
                var cond = FormatConditionParameterized(f, dialect, parameters, ref paramIndex, isDeny);
                if (!string.IsNullOrEmpty(cond))
                {
                    groupConditions.Add(cond);
                }
            }

            if (groupConditions.Count > 0)
            {
                var groupSql = groupConditions.Count == 1
                    ? groupConditions[0]
                    : $"({string.Join(" AND ", groupConditions)})";
                groupPredicates.Add(groupSql);
            }
        }

        if (groupPredicates.Count == 0) return null;

        return groupPredicates.Count == 1
            ? groupPredicates[0]
            : $"({string.Join(" OR ", groupPredicates)})";
    }

    public string FormatConditionParameterized(
        ConsentRowFilter filter,
        DatabaseDialect dialect,
        Dictionary<string, object?> parameters,
        ref int paramIndex,
        bool isDeny = false)
    {
        if (filter.FilterType == RowFilterType.SubqueryCorrelated)
        {
            return _rlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect, _subqueryStrategy, isDeny);
        }

        if (filter.FilterType == RowFilterType.CrossSourceSetFilter)
        {
            return _rlsFilterGenerator.BuildCrossSourceSetFilter(filter, 500, dialect);
        }

        if (string.Equals(filter.ValueSource, "USER_ATTRIBUTE", StringComparison.OrdinalIgnoreCase))
        {
            // RR-L4-01: Zero-Trust polarity: For ALLOW, fail closed by matching no rows (1 = 0).
            // For DENY, fail closed by matching all rows in deny predicate (1 = 1), so NOT (1 = 1) is FALSE and blocks access.
            return isDeny ? "1 = 1" : "1 = 0";
        }

        var col = filter.ColumnName;
        if (!SafeIdentifierRegex().IsMatch(col))
        {
            throw new InvalidOperationException($"Invalid column name in row filter: '{col}'. Potential injection attack.");
        }

        var quotedCol = dialect.QuoteIdentifier(col);

        var op = filter.Operator.ToUpperInvariant();
        if (!AllowedFilterOperators.Contains(op))
        {
            throw new InvalidOperationException($"Unsupported operator '{filter.Operator}' in row filter.");
        }

        var rawVal = filter.ValueJson;

        if (op == "IN")
        {
            try
            {
                using var inDoc = JsonDocument.Parse(rawVal);
                if (inDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("ValueJson for the IN operator must be a valid JSON array.");
                }

                var paramNames = new List<string>();
                foreach (var elem in inDoc.RootElement.EnumerateArray())
                {
                    var pName = $"@__gql_rls_{paramIndex++}";
                    parameters[pName] = ExtractJsonElementValue(elem);
                    paramNames.Add(pName);
                }

                if (paramNames.Count == 0) return isDeny ? "1 = 1" : "1 = 0";
                return $"{quotedCol} IN ({string.Join(", ", paramNames)})";
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("ValueJson for the IN operator must be a valid JSON array.");
            }
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawVal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"ValueJson in row filter must be valid JSON: '{rawVal}'.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Null)
            {
                return op switch
                {
                    "EQ" => $"{quotedCol} IS NULL",
                    "NEQ" => $"{quotedCol} IS NOT NULL",
                    _ => throw new InvalidOperationException($"Operator '{op}' cannot be compared with NULL.")
                };
            }

            var pName = $"@__gql_rls_{paramIndex++}";
            parameters[pName] = ExtractJsonElementValue(doc.RootElement);

            return op switch
            {
                "EQ" => $"{quotedCol} = {pName}",
                "NEQ" => $"{quotedCol} <> {pName}",
                "LT" => $"{quotedCol} < {pName}",
                "GT" => $"{quotedCol} > {pName}",
                "LTE" => $"{quotedCol} <= {pName}",
                "GTE" => $"{quotedCol} >= {pName}",
                "LIKE" => $"{quotedCol} LIKE {pName}",
                _ => $"{quotedCol} = {pName}"
            };
        }
    }

    private static object? ExtractJsonElementValue(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out var l) ? l : (elem.TryGetDecimal(out var d) ? (object)d : elem.GetDouble()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => elem.GetRawText()
        };
    }

    private string? BuildConsentFilterPredicate(IReadOnlyList<ConsentRowFilter> filters, DatabaseDialect dialect, bool isDeny = false)
    {
        if (filters.Count == 0) return null;

        // Group filters by FilterGroup: within group AND, between groups OR
        var groups = filters.GroupBy(f => f.FilterGroup).ToList();
        var groupPredicates = new List<string>();

        foreach (var group in groups)
        {
            var groupConditions = group.Select(f => FormatCondition(f, dialect, isDeny)).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (groupConditions.Count > 0)
            {
                var groupSql = groupConditions.Count == 1
                    ? groupConditions[0]
                    : $"({string.Join(" AND ", groupConditions)})";
                groupPredicates.Add(groupSql);
            }
        }

        if (groupPredicates.Count == 0) return null;

        return groupPredicates.Count == 1
            ? groupPredicates[0]
            : $"({string.Join(" OR ", groupPredicates)})";
    }

    public string FormatCondition(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer, bool isDeny = false)
    {
        if (filter.FilterType == RowFilterType.SubqueryCorrelated)
        {
            return _rlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect, _subqueryStrategy, isDeny);
        }

        if (filter.FilterType == RowFilterType.CrossSourceSetFilter)
        {
            return _rlsFilterGenerator.BuildCrossSourceSetFilter(filter, 500, dialect);
        }

        if (string.Equals(filter.ValueSource, "USER_ATTRIBUTE", StringComparison.OrdinalIgnoreCase))
        {
            // Zero-Trust: If user attributes are not resolvable in the current filter context,
            // fail closed immediately to prevent privilege escalation.
            // For ALLOW, failing closed means matching no rows (1 = 0).
            // For DENY, failing closed means matching all rows in deny predicate (1 = 1), so NOT (1 = 1) is FALSE and blocks access.
            return isDeny ? "1 = 1" : "1 = 0";
        }

        var col = filter.ColumnName;
        if (!SafeIdentifierRegex().IsMatch(col))
        {
            throw new InvalidOperationException($"Invalid column name in row filter: '{col}'. Potential injection attack.");
        }

        var quotedCol = dialect.QuoteIdentifier(col);

        var op = filter.Operator.ToUpperInvariant();
        if (!AllowedFilterOperators.Contains(op))
        {
            throw new InvalidOperationException($"Unsupported operator '{filter.Operator}' in row filter.");
        }

        var rawVal = filter.ValueJson;

        if (op == "IN")
        {
            try
            {
                using var inDoc = JsonDocument.Parse(rawVal);
                if (inDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("ValueJson for the IN operator must be a valid JSON array.");
                }

                var items = inDoc.RootElement.EnumerateArray()
                    .Select(elem => FormatLiteral(elem, dialect))
                    .ToList();

                if (items.Count == 0) return isDeny ? "1 = 1" : "1 = 0";
                return $"{quotedCol} IN ({string.Join(", ", items)})";
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("ValueJson for the IN operator must be a valid JSON array.");
            }
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawVal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"ValueJson in row filter must be valid JSON: '{rawVal}'.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Null)
            {
                return op switch
                {
                    "EQ" => $"{quotedCol} IS NULL",
                    "NEQ" => $"{quotedCol} IS NOT NULL",
                    _ => throw new InvalidOperationException($"Operator '{op}' cannot be compared with NULL.")
                };
            }

            var formattedValue = FormatLiteral(doc.RootElement, dialect);

            return op switch
            {
                "EQ" => $"{quotedCol} = {formattedValue}",
                "NEQ" => $"{quotedCol} <> {formattedValue}",
                "LT" => $"{quotedCol} < {formattedValue}",
                "GT" => $"{quotedCol} > {formattedValue}",
                "LTE" => $"{quotedCol} <= {formattedValue}",
                "GTE" => $"{quotedCol} >= {formattedValue}",
                "LIKE" => $"{quotedCol} LIKE {formattedValue}",
                _ => $"{quotedCol} = {formattedValue}"
            };
        }
    }

    private static string FormatLiteral(JsonElement elem, DatabaseDialect dialect)
    {
        return dialect.FormatSafeLiteral(elem);
    }
}
