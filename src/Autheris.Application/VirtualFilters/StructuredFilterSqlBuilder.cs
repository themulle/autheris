namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

/// <summary>
/// Virtual filters, phase 4: SQL of a structured filter (stage 1) for one protected object, in the object's dialect,
/// correlated with the reserved alias <c>autheris_target</c>. One key column follows
/// <see cref="RowFilterOptions.SubqueryStrategy"/> on SQL Server (IN or correlated IN), like consent row filters; several
/// key columns, a validity window and every other dialect use EXISTS, which works everywhere. Identifiers are validated
/// and quoted, literals escaped (numbers stay numbers).
/// </summary>
public sealed partial class StructuredFilterSqlBuilder : IVirtualFilterPredicateBuilder
{
    private readonly RowFilterSubqueryStrategy _strategy;

    public StructuredFilterSqlBuilder(IOptions<GatewayOptions>? options = null)
    {
        _strategy = options?.Value?.RowFilters?.SubqueryStrategy ?? RowFilterSubqueryStrategy.Exists;
    }

    [GeneratedRegex(@"^-?(0|[1-9]\d{0,17})(\.\d{1,18})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteral();

    public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(target);
        if (filter.Sql != null)
        {
            if (binding.ColumnMap is { Count: > 0 } || binding.TimeColumn != null)
            {
                throw new NotSupportedException($"The virtual filter '{filter.Name}' is defined in sql; a column map or time column does not apply to it.");
            }

            return SqlFilterCompiler.Compile(filter, dialect);
        }

        var definition = filter.Structured ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no definition.");

        string targetAlias = dialect.QuoteIdentifier(TrinoSqlEngine.RowFilterAliases.Target);
        string Target(string column) => $"{targetAlias}.{dialect.QuoteIdentifier(target.GetColumn(column)?.ColumnName ?? column)}";
        string Source(string qualified) => dialect.QuoteQualifiedColumn(qualified);

        // FROM ... JOIN ... WHERE <conditions>
        var from = new StringBuilder()
            .Append("FROM ").Append(dialect.FormatTableIdentifier(definition.From)).Append(" AS ").Append(dialect.QuoteIdentifier(definition.FromAlias));
        foreach (var join in definition.Joins)
        {
            from.Append(" INNER JOIN ").Append(dialect.FormatTableIdentifier(join.Table)).Append(" AS ").Append(dialect.QuoteIdentifier(join.Alias))
                .Append(" ON ").Append(Source(join.LeftColumn)).Append(" = ").Append(Source(join.RightColumn));
        }

        var conditions = definition.Where.Select(c => Condition(c, dialect, Source)).ToList();

        // key correlations: filter key column (alias.column) = target column (renamed by the binding's map)
        var keys = filter.KeyColumns
            .Select(k => (Source: Source(k), Target: Target(MapKey(binding, VirtualFilterNames.ColumnOf(k)))))
            .ToList();

        var window = new List<string>();
        if (filter.ValidFromColumn != null || filter.ValidToColumn != null)
        {
            var time = Target(binding.TimeColumn ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has a validity window; its binding needs a time column."));
            if (filter.ValidFromColumn != null)
            {
                var from_ = Source(filter.ValidFromColumn);
                window.Add($"({from_} IS NULL OR {time} >= {from_})");
            }

            if (filter.ValidToColumn != null)
            {
                var to = Source(filter.ValidToColumn);
                window.Add($"({to} IS NULL OR {time} < {to})");
            }
        }

        bool singleKeyIn = keys.Count == 1 && window.Count == 0 && dialect == DatabaseDialect.SqlServer &&
                           _strategy is RowFilterSubqueryStrategy.In or RowFilterSubqueryStrategy.InCorrelated;
        if (singleKeyIn)
        {
            var key = keys[0];
            var where = new List<string>(conditions);
            if (_strategy == RowFilterSubqueryStrategy.InCorrelated)
            {
                where.Add($"{key.Source} = {key.Target}");
            }

            return $"{key.Target} IN (SELECT {key.Source} {from}{Where(where)})";
        }

        var existsWhere = conditions
            .Concat(keys.Select(k => $"{k.Source} = {k.Target}"))
            .Concat(window)
            .ToList();
        return $"EXISTS (SELECT 1 {from}{Where(existsWhere)})";
    }

    private static string MapKey(FilterBinding binding, string key) =>
        binding.ColumnMap != null && binding.ColumnMap.TryGetValue(key, out var mapped) ? mapped : key;

    private static string Where(IReadOnlyList<string> parts) => parts.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", parts);

    private static string Condition(FilterCondition condition, DatabaseDialect dialect, Func<string, string> column)
    {
        var quoted = column(condition.Column);
        return condition.Operator switch
        {
            FilterConditionOperator.IsNull => $"{quoted} IS NULL",
            FilterConditionOperator.IsNotNull => $"{quoted} IS NOT NULL",
            FilterConditionOperator.Eq => $"{quoted} = {Literal(condition.Value!, dialect)}",
            FilterConditionOperator.NotEq => $"{quoted} <> {Literal(condition.Value!, dialect)}",
            _ => throw new InvalidOperationException($"Unsupported condition operator {condition.Operator}.")
        };
    }

    private static string Literal(string value, DatabaseDialect dialect) =>
        NumericLiteral().IsMatch(value)
            ? decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : $"'{dialect.EscapeSqlLiteral(value)}'";
}
