namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters.Federation;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

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

    private readonly ISqlEngine _engine;

    /// <param name="options">Row filter options (the subquery strategy of the legacy string path).</param>
    /// <param name="engine">Parses sql-defined filters for the typed path; a parser-only <see cref="FastSqlEngine"/> when none is injected.</param>
    public StructuredFilterSqlBuilder(IOptions<GatewayOptions>? options = null, ISqlEngine? engine = null)
    {
        _strategy = options?.Value?.RowFilters?.SubqueryStrategy ?? RowFilterSubqueryStrategy.Exists;
        _engine = engine ?? new FastSqlEngine();
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

        // FROM ... JOIN ... WHERE <conditions> (shared with the federation key query, F-GOV-14)
        var from = StructuredFilterSqlParts.From(definition, dialect);
        var conditions = definition.Where.Select(c => StructuredFilterSqlParts.Condition(c, dialect, v => Literal(v, dialect))).ToList();

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

    /// <summary>
    /// Typed variant of <see cref="Build"/> (WP-X0g): the structured definition as an <c>EXISTS</c> AST over the reserved target alias.
    /// Condition values are bound policy parameters named <paramref name="parameterPrefix"/> plus a counter (numbers stay numbers),
    /// identifiers are validated, and the SQL Server IN strategy is not used (a text-level optimisation, like WP-X0d). A filter
    /// defined in sql is compiled by <see cref="SqlFilterCompiler.CompileTyped"/> with the injected engine.
    /// </summary>
    public PolicyPredicate? BuildTyped(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect, string parameterPrefix)
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

            return SqlFilterCompiler.CompileTyped(filter, dialect, _engine, parameterPrefix);
        }

        var definition = filter.Structured ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no definition.");
        var namer = new PolicyParameterNamer(parameterPrefix);

        var innerAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TableSource from = Table(definition.From, definition.FromAlias, innerAliases, dialect);
        foreach (var join in definition.Joins)
        {
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(join.LeftColumn, "FilterJoin.LeftColumn");
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(join.RightColumn, "FilterJoin.RightColumn");
            from = new JoinedTableSource(from, JoinType.Inner, Table(join.Table, join.Alias, innerAliases, dialect),
                new OnJoinCondition(new BinaryExpression(Column(join.LeftColumn), BinaryOperator.Equal, Column(join.RightColumn))));
        }

        var conditions = new List<Expression>();
        foreach (var condition in definition.Where)
        {
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(condition.Column, "FilterCondition.Column");
            var column = Column(condition.Column);
            conditions.Add(condition.Operator switch
            {
                FilterConditionOperator.IsNull => new UnaryExpression(UnaryOperator.IsNull, column),
                FilterConditionOperator.IsNotNull => new UnaryExpression(UnaryOperator.IsNotNull, column),
                FilterConditionOperator.Eq => new BinaryExpression(column, BinaryOperator.Equal, Value(namer, condition)),
                FilterConditionOperator.NotEq => new BinaryExpression(column, BinaryOperator.NotEqual, Value(namer, condition)),
                _ => throw new InvalidOperationException($"Unsupported condition operator {condition.Operator}.")
            });
        }

        ColumnReference TargetColumn(string key)
        {
            var name = target.GetColumn(key)?.ColumnName ?? key;
            AdvancedRlsFilterGenerator.ValidateIdentifier(name, "FilterBinding.ColumnMap");
            return PolicyParameterNamer.Column($"{TrinoSqlEngine.RowFilterAliases.Target}.{name}");
        }

        foreach (var key in filter.KeyColumns)
        {
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(key, "VirtualFilter.KeyColumns");
            conditions.Add(new BinaryExpression(Column(key), BinaryOperator.Equal, TargetColumn(MapKey(binding, VirtualFilterNames.ColumnOf(key)))));
        }

        if (filter.ValidFromColumn != null || filter.ValidToColumn != null)
        {
            var timeKey = binding.TimeColumn ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has a validity window; its binding needs a time column.");
            if (filter.ValidFromColumn != null)
            {
                AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(filter.ValidFromColumn, "VirtualFilter.ValidFromColumn");
                conditions.Add(new ParenthesizedExpression(new BinaryExpression(
                    new UnaryExpression(UnaryOperator.IsNull, Column(filter.ValidFromColumn)),
                    BinaryOperator.Or,
                    new BinaryExpression(TargetColumn(timeKey), BinaryOperator.GreaterThanOrEqual, Column(filter.ValidFromColumn)))));
            }

            if (filter.ValidToColumn != null)
            {
                AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(filter.ValidToColumn, "VirtualFilter.ValidToColumn");
                conditions.Add(new ParenthesizedExpression(new BinaryExpression(
                    new UnaryExpression(UnaryOperator.IsNull, Column(filter.ValidToColumn)),
                    BinaryOperator.Or,
                    new BinaryExpression(TargetColumn(timeKey), BinaryOperator.LessThan, Column(filter.ValidToColumn)))));
            }
        }

        Expression? where = conditions.Count == 0
            ? null
            : conditions.Aggregate((left, right) => new BinaryExpression(left, BinaryOperator.And, right));
        var exists = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)], from, where, null, null),
            null, null));
        return PolicyPredicate.Create(exists, namer.Values);
    }

    private static ColumnReference Column(string qualified) => PolicyParameterNamer.Column(qualified);

    private static NamedTableSource Table(TableIdentifier table, string alias, HashSet<string> aliases, DatabaseDialect dialect)
    {
        AdvancedRlsFilterGenerator.ValidateIdentifier(table.Schema, "Schema");
        AdvancedRlsFilterGenerator.ValidateIdentifier(table.TableName, "TableName");
        AdvancedRlsFilterGenerator.ValidateIdentifier(alias, "Alias");
        if (string.Equals(alias, TrinoSqlEngine.RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase) || !aliases.Add(alias))
        {
            throw new InvalidOperationException($"Alias '{alias}' of the virtual filter collides with the target alias or another alias.");
        }

        // Like the legacy FormatTableIdentifier: a SQLite table is addressed without its schema.
        var name = dialect == DatabaseDialect.Sqlite
            ? new SqlQualifiedName(new[] { new SqlIdentifier(table.TableName, true) })
            : new SqlQualifiedName(new[] { new SqlIdentifier(table.Schema, true), new SqlIdentifier(table.TableName, true) });
        return new NamedTableSource(name, new SqlIdentifier(alias, true));
    }

    /// <summary>The value of an Eq / NotEq condition as a bound parameter; <c>NumericLiteral</c> values stay numbers like in <see cref="Literal"/>.</summary>
    private static PolicyParameterExpression Value(PolicyParameterNamer namer, FilterCondition condition)
    {
        var value = condition.Value ?? throw new InvalidOperationException($"The condition on '{condition.Column}' needs a value.");
        if (!NumericLiteral().IsMatch(value))
        {
            return namer.Add(new PolicyValue(value, SqlParameterType.String));
        }

        var number = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return number == decimal.Truncate(number) && !value.Contains('.', StringComparison.Ordinal) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            ? namer.Add(new PolicyValue(integer, SqlParameterType.Int64))
            : namer.Add(new PolicyValue(number, SqlParameterType.Decimal));
    }

    private static string MapKey(FilterBinding binding, string key) =>
        binding.ColumnMap != null && binding.ColumnMap.TryGetValue(key, out var mapped) ? mapped : key;

    private static string Where(IReadOnlyList<string> parts) => parts.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", parts);

    /// <summary>Whether a condition value is rendered as a number (shared with the federation key query).</summary>
    internal static bool IsNumericLiteral(string value) => NumericLiteral().IsMatch(value);

    private static string Literal(string value, DatabaseDialect dialect) =>
        NumericLiteral().IsMatch(value)
            ? decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : $"'{dialect.EscapeSqlLiteral(value)}'";
}
