namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// A paged read has no deterministic order (no orderable column readable in clear text). An <see cref="ArgumentException"/>
/// (client error); the fixed message names no column.
/// </summary>
public sealed class UnorderedPagingException : ArgumentException
{
    public UnorderedPagingException(string? paramName)
        : base(GovernedTableReadPlanner.NoOrderableColumnMessage, paramName)
    {
    }
}

/// <summary>
/// WP-ARC-B-01: the shared table-read planner extracted from <c>GovernedDataQueryService</c> (column authorization,
/// oracle-inference guard on filter and order columns, SQL formulation). Stateless and thread-safe.
/// <para>
/// No policy text is spliced here: the decision authorizes the request and drives column access; the row policy and the
/// tenant restriction are applied exactly once, by the compiler on the governed path. Identifiers are emitted as quoted
/// catalog spellings and every value travels as a client parameter (or, for the REST text translator, as a literal that the
/// compiler binds).
/// </para>
/// </summary>
public sealed partial class GovernedTableReadPlanner : IGovernedTableReadPlanner
{
    /// <summary>Alias of the single column of <see cref="PlanCount"/>.</summary>
    public const string CountColumnAlias = "total_count";

    /// <summary>Mandatory per-group row count alias emitted by <see cref="PlanAggregate"/> for minimum group size guarding (#37).</summary>
    public const string GroupCountColumnAlias = "__n";

    public string BuildFullTableSelect(TableIdentifier table) => $"SELECT * FROM {table.ToQualifiedName()}";

    public PlannedTableRead Plan(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        if (intent.Limit <= 0)
        {
            throw new ArgumentException("The read limit must be positive.", nameof(intent));
        }

        EnsureAllowed(intent, decision);
        EnsureRequiredFilterColumnsSatisfied(metadata, decision, intent.Filter);
        var columnsToSelect = ResolveColumns(intent, metadata, decision);
        var predicate = ResolveFilter(intent, metadata, decision);
        var (orderBy, ordering) = ResolveOrderBy(intent, metadata, decision);

        var sb = new StringBuilder();
        sb.Append("SELECT ")
          .Append(string.Join(", ", columnsToSelect.Select(c => DataApiFilterTranslator.QuoteIdentifier(metadata.GetColumn(c)!.ColumnName))))
          .Append(" FROM ")
          .Append(intent.Table.ToString());

        if (predicate != null)
        {
            sb.Append(" WHERE ").Append(predicate);
        }

        if (orderBy.Count > 0)
        {
            sb.Append(" ORDER BY ").Append(string.Join(", ", orderBy));
        }

        // Trino-form pagination: the governed compile path parses OFFSET n ROWS LIMIT m and every dialect generator emits its
        // native form (capability PaginationStyle).
        if (intent.Offset > 0)
        {
            sb.Append(" OFFSET ").Append(intent.Offset.ToString(CultureInfo.InvariantCulture)).Append(" ROWS");
        }

        sb.Append(" LIMIT ").Append(intent.Limit.ToString(CultureInfo.InvariantCulture));

        return new PlannedTableRead(sb.ToString(), ClientParameters(intent), columnsToSelect, ordering);
    }

    public PlannedTableRead PlanCount(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);

        EnsureAllowed(intent, decision);
        EnsureRequiredFilterColumnsSatisfied(metadata, decision, intent.Filter);
        var predicate = ResolveFilter(intent, metadata, decision);

        var sb = new StringBuilder();
        sb.Append("SELECT COUNT(*) AS ").Append(DataApiFilterTranslator.QuoteIdentifier(CountColumnAlias))
          .Append(" FROM ").Append(intent.Table.ToString());
        if (predicate != null)
        {
            sb.Append(" WHERE ").Append(predicate);
        }

        return new PlannedTableRead(sb.ToString(), ClientParameters(intent), Array.Empty<string>());
    }

    public PlannedTableAggregate PlanAggregate(TableAggregateIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        if (intent.Limit <= 0)
        {
            throw new ArgumentException("The aggregate group limit must be positive.", nameof(intent));
        }

        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by governance policy.";
            throw new UnauthorizedAccessException($"Access to table '{intent.Table.ToQualifiedName()}' is forbidden: {reasons}");
        }

        EnsureRequiredFilterColumnsSatisfied(metadata, decision, intent.Filter, isAggregate: true);

        var groupByCols = new List<string>();
        if (intent.GroupBy is { Count: > 0 })
        {
            foreach (var rawCol in intent.GroupBy)
            {
                var colMeta = metadata.GetColumn(rawCol)
                    ?? throw UnknownColumn(rawCol, intent);

                var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
                if (access == ColumnAccessLevel.Mask)
                {
                    throw new MaskedAggregationException(colMeta.ColumnName);
                }

                if (access != ColumnAccessLevel.Clear)
                {
                    throw UnknownColumn(rawCol, intent);
                }

                if (!DefaultReadOrdering.IsOrderableType(colMeta.DataType, metadata.Dialect))
                {
                    throw new ArgumentException($"Column '{rawCol}' has non-groupable type '{colMeta.DataType}'.", nameof(intent));
                }

                if (!groupByCols.Contains(colMeta.ColumnName, StringComparer.OrdinalIgnoreCase))
                {
                    groupByCols.Add(colMeta.ColumnName);
                }
            }
        }

        if (intent.TimeBuckets is { Count: > 0 })
        {
            foreach (var tb in intent.TimeBuckets)
            {
                var colMeta = metadata.GetColumn(tb.Column) ?? throw UnknownColumn(tb.Column, intent);
                var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
                if (access == ColumnAccessLevel.Mask)
                {
                    throw new MaskedAggregationException(colMeta.ColumnName, $"Time-bucketing on masked column '{colMeta.ColumnName}' is forbidden.");
                }
                if (access != ColumnAccessLevel.Clear)
                {
                    throw UnknownColumn(tb.Column, intent);
                }
            }
        }

        var timeBucketMap = intent.TimeBuckets?
            .ToDictionary(b => b.Column, b => b, StringComparer.OrdinalIgnoreCase);

        var resolvedAggregates = new List<AggregateSelection>();
        var selectItems = new List<string>();
        var groupByExpressions = new List<string>();
        foreach (var gCol in groupByCols)
        {
            if (timeBucketMap != null && timeBucketMap.TryGetValue(gCol, out var bucketSpec))
            {
                var (selectExpr, groupByExpr) = BuildTimeBucketExpression(gCol, bucketSpec, metadata.Dialect);
                selectItems.Add(selectExpr);
                groupByExpressions.Add(groupByExpr);
            }
            else
            {
                selectItems.Add(DataApiFilterTranslator.QuoteIdentifier(gCol));
                groupByExpressions.Add(DataApiFilterTranslator.QuoteIdentifier(gCol));
            }
        }

        selectItems.Add($"COUNT(*) AS {DataApiFilterTranslator.QuoteIdentifier(GroupCountColumnAlias)}");

        if (intent.Aggregates is { Count: > 0 })
        {
            foreach (var agg in intent.Aggregates)
            {
                if (agg.Column is null)
                {
                    if (agg.Function != AggregateFunction.Count)
                    {
                        throw new ArgumentException($"Aggregate function '{agg.Function}' requires a target column.", nameof(intent));
                    }

                    var countSelection = new AggregateSelection(AggregateFunction.Count, null, agg.EffectiveAlias);
                    resolvedAggregates.Add(countSelection);
                    selectItems.Add($"COUNT(*) AS {DataApiFilterTranslator.QuoteIdentifier(countSelection.EffectiveAlias)}");
                    continue;
                }

                var colMeta = metadata.GetColumn(agg.Column)
                    ?? throw UnknownColumn(agg.Column, intent);

                var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
                if (access == ColumnAccessLevel.Mask)
                {
                    throw new MaskedAggregationException(colMeta.ColumnName);
                }

                if (access != ColumnAccessLevel.Clear)
                {
                    throw UnknownColumn(agg.Column, intent);
                }

                if (agg.Function is AggregateFunction.Sum or AggregateFunction.Avg && !IsNumericDataType(colMeta.DataType))
                {
                    throw new ArgumentException(
                        $"Aggregate function '{agg.Function}' requires a numeric column, but '{colMeta.ColumnName}' has type '{colMeta.DataType}'.",
                        nameof(intent));
                }

                var normalized = new AggregateSelection(agg.Function, colMeta.ColumnName, agg.EffectiveAlias);
                resolvedAggregates.Add(normalized);

                var quotedCol = DataApiFilterTranslator.QuoteIdentifier(colMeta.ColumnName);
                var quotedAlias = DataApiFilterTranslator.QuoteIdentifier(normalized.EffectiveAlias);
                var sqlExpr = agg.Function switch
                {
                    AggregateFunction.Count => $"COUNT({quotedCol}) AS {quotedAlias}",
                    AggregateFunction.CountDistinct => $"COUNT(DISTINCT {quotedCol}) AS {quotedAlias}",
                    AggregateFunction.Sum => $"SUM({quotedCol}) AS {quotedAlias}",
                    AggregateFunction.Avg => $"AVG({quotedCol}) AS {quotedAlias}",
                    AggregateFunction.Min => $"MIN({quotedCol}) AS {quotedAlias}",
                    AggregateFunction.Max => $"MAX({quotedCol}) AS {quotedAlias}",
                    _ => throw new ArgumentOutOfRangeException(nameof(intent), $"Unsupported aggregate function '{agg.Function}'.")
                };
                selectItems.Add(sqlExpr);
            }
        }

        string? predicate = null;
        if (intent.Filter != null)
        {
            foreach (var col in intent.Filter.ReferencedColumns)
            {
                var colMeta = metadata.GetColumn(col);
                var access = colMeta == null ? ColumnAccessLevel.Deny : decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
                if (colMeta == null || access == ColumnAccessLevel.Deny)
                {
                    throw UnknownColumn(col, intent);
                }

                if (access == ColumnAccessLevel.Mask)
                {
                    throw new MaskedAggregationException(colMeta.ColumnName, $"Filtering on masked column '{col}' in an aggregation query is forbidden.");
                }

                if (access != ColumnAccessLevel.Clear)
                {
                    throw new ArgumentException(
                        $"Filtering on masked or denied column '{col}' is rejected to prevent oracle inference attacks.",
                        nameof(intent));
                }
            }

            var rendered = intent.Filter.GetTrinoPredicate(name => metadata.GetColumn(name)?.ColumnName ?? name);
            predicate = string.IsNullOrWhiteSpace(rendered) ? null : rendered;
        }

        var orderByItems = new List<string>();
        if (intent.OrderBy is { Count: > 0 })
        {
            foreach (var item in intent.OrderBy)
            {
                if (groupByCols.Any(g => string.Equals(g, item.Column, StringComparison.OrdinalIgnoreCase)))
                {
                    var canonical = groupByCols.First(g => string.Equals(g, item.Column, StringComparison.OrdinalIgnoreCase));
                    var quoted = DataApiFilterTranslator.QuoteIdentifier(canonical);
                    orderByItems.Add(item.Descending ? quoted + " DESC" : quoted);
                }
                else if (resolvedAggregates.Any(a => string.Equals(a.EffectiveAlias, item.Column, StringComparison.OrdinalIgnoreCase)))
                {
                    var canonical = resolvedAggregates.First(a => string.Equals(a.EffectiveAlias, item.Column, StringComparison.OrdinalIgnoreCase)).EffectiveAlias;
                    var quoted = DataApiFilterTranslator.QuoteIdentifier(canonical);
                    orderByItems.Add(item.Descending ? quoted + " DESC" : quoted);
                }
                else if (string.Equals(item.Column, "count", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(item.Column, GroupCountColumnAlias, StringComparison.OrdinalIgnoreCase))
                {
                    var quoted = DataApiFilterTranslator.QuoteIdentifier(GroupCountColumnAlias);
                    orderByItems.Add(item.Descending ? quoted + " DESC" : quoted);
                }
                else
                {
                    throw new ArgumentException(
                        $"Aggregate ORDER BY column '{item.Column}' must be one of the GROUP BY columns or aggregate aliases.",
                        nameof(intent));
                }
            }
        }
        else if (groupByCols.Count > 0)
        {
            orderByItems.AddRange(groupByCols.Select(DataApiFilterTranslator.QuoteIdentifier));
        }

        var sb = new StringBuilder();
        sb.Append("SELECT ")
          .Append(string.Join(", ", selectItems))
          .Append(" FROM ")
          .Append(intent.Table.ToString());

        if (predicate != null)
        {
            sb.Append(" WHERE ").Append(predicate);
        }

        if (groupByExpressions.Count > 0)
        {
            sb.Append(" GROUP BY ")
              .Append(string.Join(", ", groupByExpressions));
        }

        if (orderByItems.Count > 0)
        {
            sb.Append(" ORDER BY ").Append(string.Join(", ", orderByItems));
        }

        sb.Append(" LIMIT ").Append(intent.Limit.ToString(CultureInfo.InvariantCulture));

        var parameters = intent.Filter == null || intent.Filter.Parameters.Count == 0
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>((IEnumerable<KeyValuePair<string, object?>>)intent.Filter.Parameters, StringComparer.Ordinal);

        return new PlannedTableAggregate(sb.ToString(), parameters, groupByCols, resolvedAggregates, GroupCountColumnAlias);
    }

    private static bool IsNumericDataType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return true;
        }

        var dt = dataType.Trim().ToLowerInvariant();
        return dt.Contains("int", StringComparison.Ordinal) ||
               dt.Contains("numeric", StringComparison.Ordinal) ||
               dt.Contains("decimal", StringComparison.Ordinal) ||
               dt.Contains("number", StringComparison.Ordinal) ||
               dt.Contains("float", StringComparison.Ordinal) ||
               dt.Contains("double", StringComparison.Ordinal) ||
               dt.Contains("real", StringComparison.Ordinal) ||
               dt.Contains("money", StringComparison.Ordinal) ||
               dt.Contains("serial", StringComparison.Ordinal) ||
               dt.Contains("hugeint", StringComparison.Ordinal);
    }

    private static void EnsureAllowed(TableReadIntent intent, TableAccessDecision decision)
    {
        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by governance policy.";
            throw new UnauthorizedAccessException($"Access to table '{intent.Table.ToQualifiedName()}' is forbidden: {reasons}");
        }
    }

    private static IReadOnlyList<string> ResolveColumns(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        if (intent.SelectColumns is { Count: > 0 } requested)
        {
            foreach (var col in requested)
            {
                var colMeta = metadata.GetColumn(col);
                if (colMeta == null || decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata) == ColumnAccessLevel.Deny)
                {
                    throw UnknownColumn(col, intent);
                }
            }

            return requested;
        }

        var accessible = metadata.Columns
            .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();
        if (accessible.Count == 0)
        {
            throw new UnauthorizedAccessException($"No accessible columns available on table '{intent.Table.ToQualifiedName()}'.");
        }

        return accessible;
    }

    /// <summary>The Trino predicate of the filter, or null. Filter columns must exist and be readable in clear text.</summary>
    private static string? ResolveFilter(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        if (intent.Filter == null)
        {
            return null;
        }

        foreach (var col in intent.Filter.ReferencedColumns)
        {
            var colMeta = metadata.GetColumn(col);
            var access = colMeta == null ? ColumnAccessLevel.Deny : decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (colMeta == null || access == ColumnAccessLevel.Deny)
            {
                throw UnknownColumn(col, intent);
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new ArgumentException(
                    $"Filtering on masked or denied column '{col}' is rejected to prevent oracle inference attacks.",
                    nameof(intent));
            }
        }

        var predicate = intent.Filter.GetTrinoPredicate(name => metadata.GetColumn(name)?.ColumnName ?? name);
        return string.IsNullOrWhiteSpace(predicate) ? null : predicate;
    }

    /// <summary>Text of the 400 for a paged read that has no deterministic order. Reveals no column name.</summary>
    public const string NoOrderableColumnMessage = "Paging is not possible: no orderable column readable in clear text is available to order by.";

    /// <summary>
    /// ORDER BY items (quoted catalog spellings) and the rule behind them. An explicit order gets the default ordering columns
    /// as tiebreaker (clear columns only); without an explicit order the shared <see cref="DefaultReadOrdering"/> applies.
    /// </summary>
    private static (List<string> Items, DefaultOrderingKind Kind) ResolveOrderBy(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        var items = new List<string>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultOrdering = DefaultReadOrdering.Resolve(metadata, decision);
        if (intent.OrderBy is { Count: > 0 } requested)
        {
            foreach (var item in requested)
            {
                var colMeta = metadata.GetColumn(item.Column);
                var access = colMeta == null ? ColumnAccessLevel.Deny : decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
                if (colMeta == null || access == ColumnAccessLevel.Deny)
                {
                    throw UnknownColumn(item.Column, intent);
                }

                if (access != ColumnAccessLevel.Clear)
                {
                    throw new ArgumentException(
                        $"Ordering on masked or denied column '{item.Column}' is rejected to prevent oracle inference attacks.",
                        nameof(intent));
                }

                named.Add(colMeta.ColumnName);
                var quoted = DataApiFilterTranslator.QuoteIdentifier(colMeta.ColumnName);
                items.Add(item.Descending ? quoted + " DESC" : quoted);
            }

            foreach (var column in defaultOrdering.Columns)
            {
                if (named.Add(column))
                {
                    items.Add(DataApiFilterTranslator.QuoteIdentifier(column));
                }
            }

            return (items, DefaultOrderingKind.Explicit);
        }

        if (defaultOrdering.Kind == DefaultOrderingKind.None && intent.Offset > 0)
        {
            throw new UnorderedPagingException(nameof(intent));
        }

        items.AddRange(defaultOrdering.Columns.Select(DataApiFilterTranslator.QuoteIdentifier));
        return (items, defaultOrdering.Kind);
    }

    /// <summary>One text for an unknown column and for a column the caller may not see, so the message is no existence oracle.</summary>
    private static ArgumentException UnknownColumn(string column, TableReadIntent intent) =>
        new($"Column '{column}' does not exist on table '{intent.Table.ToQualifiedName()}'.", nameof(intent));

    private static ArgumentException UnknownColumn(string column, TableAggregateIntent intent) =>
        new($"Column '{column}' does not exist on table '{intent.Table.ToQualifiedName()}'.", nameof(intent));

    private static IReadOnlyDictionary<string, object?> ClientParameters(TableReadIntent intent) =>
        intent.Filter == null || intent.Filter.Parameters.Count == 0
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>((IEnumerable<KeyValuePair<string, object?>>)intent.Filter.Parameters, StringComparer.Ordinal);

    private static void EnsureRequiredFilterColumnsSatisfied(
        TableMetadata metadata,
        TableAccessDecision decision,
        TableFilterClause? filter,
        bool isAggregate = false)
    {
        if (metadata.RequiredFilterColumns.Length == 0 && metadata.RequiredFilterColumnGroups.Length == 0)
        {
            return;
        }

        bool IsSatisfied(string reqCol)
        {
            if (!isAggregate &&
                metadata.TenantColumnName != null &&
                string.Equals(metadata.TenantColumnName, reqCol, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (decision.RowPolicy is Autheris.Application.Policy.PolicyPredicateRowPolicy rls &&
                rls.Predicate.ReferencedColumns.Contains(reqCol.ToLowerInvariant()))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) &&
                decision.CombinedRowFilterSql.Contains(reqCol, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (filter != null &&
                filter.ReferencedColumns.Any(c => string.Equals(c, reqCol, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        if (metadata.RequiredFilterColumnGroups.Length > 0)
        {
            bool anyGroupSatisfied = false;
            foreach (var group in metadata.RequiredFilterColumnGroups)
            {
                if (group.Length == 0) continue;
                if (group.All(IsSatisfied))
                {
                    anyGroupSatisfied = true;
                    break;
                }
            }

            if (!anyGroupSatisfied)
            {
                throw new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException(
                    TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(
                        TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing));
            }
        }

        if (metadata.RequiredFilterColumns.Length > 0)
        {
            foreach (var reqCol in metadata.RequiredFilterColumns)
            {
                if (IsSatisfied(reqCol))
                {
                    continue;
                }

                throw new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException(
                    TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(
                        TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing));
            }
        }
    }

    private static (string SelectExpr, string GroupByExpr) BuildTimeBucketExpression(
        string columnName,
        TimeBucketSpecification spec,
        DatabaseDialect dialect)
    {
        var unit = spec.Interval switch
        {
            TimeBucketInterval.Hour => "hour",
            TimeBucketInterval.Day => "day",
            TimeBucketInterval.Week => "week",
            TimeBucketInterval.Month => "month",
            TimeBucketInterval.Quarter => "quarter",
            TimeBucketInterval.Year => "year",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), $"Unsupported interval '{spec.Interval}'.")
        };

        var quotedCol = dialect.QuoteIdentifier(columnName);
        var timeZone = string.IsNullOrWhiteSpace(spec.TimeZone) ? "UTC" : spec.TimeZone;

        if (!TrinoSqlEngine.TimeZoneRegistry.Contains(timeZone))
        {
            throw new ArgumentException($"Time zone '{timeZone}' is not recognized.");
        }

        switch (dialect)
        {
            case DatabaseDialect.PostgreSql:
            {
                TrinoSqlEngine.TimeZoneRegistry.TryResolve(
                    TrinoSqlEngine.TargetSqlDialect.PostgreSql,
                    timeZone,
                    out var dialectZone);
                var zone = string.IsNullOrWhiteSpace(dialectZone) ? timeZone : dialectZone;
                var expr = $"date_trunc('{unit}', {quotedCol} AT TIME ZONE '{zone}')";
                return ($"{expr} AS {quotedCol}", expr);
            }
            case DatabaseDialect.SqlServer:
            {
                TrinoSqlEngine.TimeZoneRegistry.TryResolve(
                    TrinoSqlEngine.TargetSqlDialect.SqlServer,
                    timeZone,
                    out var winZone);
                var zone = string.IsNullOrWhiteSpace(winZone) ? timeZone : winZone;
                var expr = $"DATETRUNC({unit}, {quotedCol} AT TIME ZONE '{zone}')";
                return ($"{expr} AS {quotedCol}", expr);
            }
            case DatabaseDialect.Sqlite:
            {
                string fmt = unit switch
                {
                    "hour" => "%Y-%m-%d %H:00:00",
                    "day" => "%Y-%m-%d",
                    "week" => "%Y-%W",
                    "month" => "%Y-%m-01",
                    "quarter" => "%Y-m",
                    "year" => "%Y-01-01",
                    _ => "%Y-%m-%d"
                };

                string expr;
                if (string.Equals(timeZone, "UTC", StringComparison.OrdinalIgnoreCase))
                {
                    expr = $"strftime('{fmt}', datetime({quotedCol}))";
                }
                else
                {
                    TimeSpan offset = TimeSpan.Zero;
                    try
                    {
                        var tzi = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                        offset = tzi.BaseUtcOffset;
                    }
                    catch
                    {
                        // Fallback
                    }
                    var offsetStr = (offset >= TimeSpan.Zero ? "+" : "-") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                    expr = $"strftime('{fmt}', datetime({quotedCol}, '{offsetStr}'))";
                }
                return ($"{expr} AS {quotedCol}", expr);
            }
            default:
            {
                var expr = $"date_trunc('{unit}', {quotedCol})";
                return ($"{expr} AS {quotedCol}", expr);
            }
        }
    }
}
