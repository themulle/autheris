using System.Collections;
using System.Text;
using Autheris.Domain.Exceptions;

namespace Autheris.Application.Sql.Tree;

/// <summary>What the renderer needs from its caller: dialect-specific column quoting, value binding and the LIKE escape character.</summary>
internal interface ITreeFilterTarget
{
    /// <summary>The SQL text that references <paramref name="column"/> (already quoted).</summary>
    string Column(string column);

    /// <summary>Binds <paramref name="value"/> and returns the parameter name to put into the text.</summary>
    string AddParameter(object? value);

    /// <summary>The character that escapes LIKE wildcards; it travels as an explicit ESCAPE clause.</summary>
    char LikeEscape { get; }

    /// <summary>The LIKE special characters that are escaped (the escape character itself is always escaped).</summary>
    string LikeSpecials { get; }

    /// <summary>
    /// False: a LIKE whose pattern needed no escaping omits the ESCAPE clause (the in-memory evaluator cannot read one, and an escaped
    /// pattern keeps the clause so that it denies instead of mis-evaluating).
    /// </summary>
    bool AlwaysEmitLikeEscape { get; }
}

/// <summary>
/// The pure shape logic of a typed filter tree (AND/OR/NOT nesting, IN list limit, depth limit, null rules, LIKE escaping),
/// shared by <see cref="TreeSqlCompiler"/> (dialect SQL) and <see cref="TreeFilterClauseBuilder"/> (Trino). Every value is bound
/// through the target; no value ever enters the text.
/// </summary>
internal static class TreeFilterRenderer
{
    public static string Render(TreeFilter filter, ITreeFilterTarget target, int depth = 0, bool negated = false)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(target);

        if (depth > TreeSqlCompiler.MaxFilterDepth)
        {
            throw new GatewayInvalidQueryException($"The filter is nested deeper than {TreeSqlCompiler.MaxFilterDepth} levels.");
        }

        switch (filter)
        {
            case TreeAndFilter and:
                return and.Items.Count == 0
                    ? "1 = 1"
                    : "(" + string.Join(" AND ", and.Items.Select(i => Render(i, target, depth + 1, negated))) + ")";
            case TreeOrFilter or:
                return or.Items.Count == 0
                    ? "1 = 0"
                    : "(" + string.Join(" OR ", or.Items.Select(i => Render(i, target, depth + 1, negated))) + ")";
            case TreeNotFilter not:
                return "NOT (" + Render(not.Item, target, depth + 1, !negated) + ")";
            case TreeComparison comparison:
                return RenderComparison(comparison, target, negated);
            default:
                throw new GatewayInvalidQueryException("Unsupported filter.");
        }
    }

    private static string RenderComparison(TreeComparison comparison, ITreeFilterTarget target, bool negated)
    {
        var column = target.Column(comparison.Column);
        var value = comparison.Value;

        if (comparison.BindNullOperand && value is null && TryRenderNullOperand(comparison.Operator, column, target) is { } nullComparison)
        {
            return nullComparison;
        }

        switch (comparison.Operator)
        {
            case TreeFilterOperator.IsNull:
                return value is true ? $"{column} IS NULL" : $"{column} IS NOT NULL";
            case TreeFilterOperator.Eq:
                return value == null ? $"{column} IS NULL" : $"{column} = {target.AddParameter(value)}";
            case TreeFilterOperator.Neq:
                return value == null ? $"{column} IS NOT NULL" : $"{column} <> {target.AddParameter(value)}";
            case TreeFilterOperator.Gt:
                return $"{column} > {target.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.Gte:
                return $"{column} >= {target.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.Lt:
                return $"{column} < {target.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.Lte:
                return $"{column} <= {target.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.In:
            case TreeFilterOperator.NotIn:
            {
                var items = ToList(comparison, out var hasNull);
                if (hasNull)
                {
                    // SEC-RG-16 (#328): x NOT IN (..., NULL) is never true under three-valued logic, and x IN (..., NULL) is true or unknown. Under an
                    // odd number of enclosing NOTs the term is rendered so that its negation is that same never-wider answer: the negated NOT IN
                    // keeps its matches (NOT (x IN (...))), the negated IN is true so that its NOT is false.
                    if (comparison.Operator == TreeFilterOperator.NotIn)
                    {
                        if (!negated)
                        {
                            return "1 = 0";
                        }

                        return items.Count == 0
                            ? "1 = 1"
                            : $"NOT ({column} IN ({string.Join(", ", items.Select(target.AddParameter))}))";
                    }

                    if (negated)
                    {
                        return "1 = 1";
                    }
                }

                if (items.Count == 0)
                {
                    return comparison.Operator == TreeFilterOperator.In ? "1 = 0" : "1 = 1";
                }
                var names = string.Join(", ", items.Select(target.AddParameter));
                return comparison.Operator == TreeFilterOperator.In ? $"{column} IN ({names})" : $"{column} NOT IN ({names})";
            }
            case TreeFilterOperator.Contains:
                return Like(column, RequireText(comparison), "%", "%", target);
            case TreeFilterOperator.StartsWith:
                return Like(column, RequireText(comparison), string.Empty, "%", target);
            case TreeFilterOperator.EndsWith:
                return Like(column, RequireText(comparison), "%", string.Empty, target);
            default:
                throw new GatewayInvalidQueryException("Unsupported filter operator.");
        }
    }

    // SEC-PG-06: the NULL operand is a bound parameter (never `= NULL` text, never a FALSE constant), so NOT/OR keep three-valued logic.
    private static string? TryRenderNullOperand(TreeFilterOperator op, string column, ITreeFilterTarget target)
    {
        var symbol = op switch
        {
            TreeFilterOperator.Eq => "=",
            TreeFilterOperator.Neq => "<>",
            TreeFilterOperator.Gt => ">",
            TreeFilterOperator.Gte => ">=",
            TreeFilterOperator.Lt => "<",
            TreeFilterOperator.Lte => "<=",
            TreeFilterOperator.Contains or TreeFilterOperator.StartsWith or TreeFilterOperator.EndsWith => "LIKE",
            _ => null
        };
        if (symbol is null)
        {
            return null;
        }

        // The NULL parameter is intentionally untyped: the column it is compared with types it.
        var clause = $"{column} {symbol} {target.AddParameter(null)}";
        return symbol == "LIKE" && target.AlwaysEmitLikeEscape ? clause + $" ESCAPE '{target.LikeEscape}'" : clause;
    }

    private static string Like(string column, string text, string prefix, string suffix, ITreeFilterTarget target)
    {
        var escaped = EscapeLike(text, target);
        var pattern = prefix + escaped + suffix;
        var clause = $"{column} LIKE {target.AddParameter(pattern)}";
        return target.AlwaysEmitLikeEscape || !string.Equals(escaped, text, StringComparison.Ordinal)
            ? clause + $" ESCAPE '{target.LikeEscape}'"
            : clause;
    }

    private static string EscapeLike(string value, ITreeFilterTarget target)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch == target.LikeEscape || target.LikeSpecials.Contains(ch, StringComparison.Ordinal))
            {
                sb.Append(target.LikeEscape);
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static object RequireValue(TreeComparison comparison) =>
        comparison.Value ?? throw new GatewayInvalidQueryException($"The operator '{comparison.Operator}' needs a value.");

    private static string RequireText(TreeComparison comparison) =>
        comparison.Value as string ?? throw new GatewayInvalidQueryException($"The operator '{comparison.Operator}' needs a text value.");

    private static List<object?> ToList(TreeComparison comparison, out bool hasNull)
    {
        if (comparison.Value is string || comparison.Value is not IEnumerable enumerable)
        {
            throw new GatewayInvalidQueryException($"The operator '{comparison.Operator}' needs a list.");
        }
        var all = enumerable.Cast<object?>().ToList();
        hasNull = all.Any(v => v == null);
        var items = all.Where(v => v != null).Distinct().ToList();
        if (items.Count > TreeSqlCompiler.MaxInListSize)
        {
            throw new GatewayInvalidQueryException($"A list filter may contain at most {TreeSqlCompiler.MaxInListSize} values.");
        }
        return items;
    }
}
