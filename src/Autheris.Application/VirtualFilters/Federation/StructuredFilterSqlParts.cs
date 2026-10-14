namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Text;

/// <summary>
/// Shared rendering of a structured Virtual Filter definition (stage 1): the <c>FROM ... INNER JOIN ...</c> clause and
/// the conditions. Used by the SQL-channel predicate (<see cref="StructuredFilterSqlBuilder"/>, literals) and by the
/// key query of the federation (<see cref="StructuredFilterKeyQueryBuilder"/>, bound parameters), so both evaluate the
/// same definition. Identifiers are validated and quoted per dialect.
/// </summary>
internal static class StructuredFilterSqlParts
{
    /// <summary><c>FROM &lt;from&gt; AS &lt;alias&gt; [INNER JOIN &lt;table&gt; AS &lt;alias&gt; ON &lt;left&gt; = &lt;right&gt;]...</c></summary>
    internal static string From(StructuredFilterDefinition definition, DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var from = new StringBuilder()
            .Append("FROM ").Append(dialect.FormatTableIdentifier(definition.From)).Append(" AS ").Append(dialect.QuoteIdentifier(definition.FromAlias));
        foreach (var join in definition.Joins)
        {
            from.Append(" INNER JOIN ").Append(dialect.FormatTableIdentifier(join.Table)).Append(" AS ").Append(dialect.QuoteIdentifier(join.Alias))
                .Append(" ON ").Append(dialect.QuoteQualifiedColumn(join.LeftColumn)).Append(" = ").Append(dialect.QuoteQualifiedColumn(join.RightColumn));
        }

        return from.ToString();
    }

    /// <summary>One condition; <paramref name="value"/> renders the right-hand side of <c>=</c> / <c>&lt;&gt;</c>.</summary>
    internal static string Condition(FilterCondition condition, DatabaseDialect dialect, Func<string, string> value)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(value);
        var quoted = dialect.QuoteQualifiedColumn(condition.Column);
        return condition.Operator switch
        {
            FilterConditionOperator.IsNull => $"{quoted} IS NULL",
            FilterConditionOperator.IsNotNull => $"{quoted} IS NOT NULL",
            FilterConditionOperator.Eq => $"{quoted} = {value(condition.Value ?? throw MissingValue(condition))}",
            FilterConditionOperator.NotEq => $"{quoted} <> {value(condition.Value ?? throw MissingValue(condition))}",
            _ => throw new InvalidOperationException($"Unsupported condition operator {condition.Operator}.")
        };
    }

    private static InvalidOperationException MissingValue(FilterCondition condition) =>
        new($"The condition on '{condition.Column}' needs a value.");
}
