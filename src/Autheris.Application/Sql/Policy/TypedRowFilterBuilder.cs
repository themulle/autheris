namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Builds the typed row predicate of consent row filters (WP-X0d). Semantics are those of <see cref="RowFilterSqlBuilder"/>:
/// conditions of one <c>FilterGroup</c> are AND-combined, groups OR-combined, allow consents OR-combined and deny consents
/// excluded with <c>NOT (...)</c>. Every value is a policy parameter; an <c>IN</c> list has one parameter per item.
/// </summary>
public sealed partial class TypedRowFilterBuilder
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeIdentifierRegex();

    private static readonly HashSet<string> AllowedOperators = new(StringComparer.Ordinal)
    {
        "EQ", "NEQ", "LT", "GT", "LTE", "GTE", "LIKE", "IN"
    };

    /// <summary>One consent's filters as a predicate. <paramref name="dialect"/> only bounds the IN-list size (Oracle: 1000 items per list).</summary>
    /// <exception cref="ArgumentException">No filters: the caller decides what "no restriction" means, this builder never guesses.</exception>
    public PolicyPredicate Build(IReadOnlyList<ConsentRowFilter> filters, TableIdentity table, TargetSqlDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Count == 0)
        {
            throw new ArgumentException("At least one row filter is required.", nameof(filters));
        }

        var namer = new PolicyParameterNamer();
        var expression = ConsentExpression(filters, dialect, namer, isDeny: false)
            ?? throw new InvalidOperationException($"The row filters of table '{table}' produced no condition.");
        return PolicyPredicate.Create(expression, namer.Values);
    }

    /// <summary>
    /// Allow consents OR-combined (an allow consent without filters means all rows), deny consents as <c>NOT (...)</c>.
    /// Returns null when no consent restricts rows.
    /// </summary>
    public PolicyPredicate? BuildCombined(IReadOnlyList<Consent> aConsents, IReadOnlyList<Consent> dConsents, TableIdentity table, TargetSqlDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(aConsents);
        ArgumentNullException.ThrowIfNull(dConsents);
        var namer = new PolicyParameterNamer();

        Expression? allow = null;
        if (!aConsents.Any(c => c.RowFilters.Count == 0))
        {
            allow = OrChain(aConsents.Select(c => ConsentExpression(c.RowFilters, dialect, namer, isDeny: false)));
        }

        Expression? deny = OrChain(dConsents.Where(c => c.RowFilters.Count > 0).Select(c => ConsentExpression(c.RowFilters, dialect, namer, isDeny: true)));
        if (deny != null)
        {
            // The generator parenthesizes the operand of NOT itself.
            deny = new UnaryExpression(UnaryOperator.Not, deny);
        }

        Expression? combined = (allow, deny) switch
        {
            (not null, not null) => new ParenthesizedExpression(new BinaryExpression(allow, BinaryOperator.And, deny)),
            (not null, null) => allow,
            (null, not null) => deny,
            _ => null
        };

        return combined is null ? null : PolicyPredicate.Create(combined, namer.Values);
    }

    private static Expression? OrChain(IEnumerable<Expression?> parts)
    {
        var list = parts.Where(p => p != null).Select(p => p!).ToList();
        return list.Count switch
        {
            0 => null,
            1 => list[0],
            _ => new ParenthesizedExpression(list.Aggregate((l, r) => new BinaryExpression(l, BinaryOperator.Or, r)))
        };
    }

    private static Expression? ConsentExpression(IReadOnlyList<ConsentRowFilter> filters, TargetSqlDialect? dialect, PolicyParameterNamer namer, bool isDeny)
    {
        if (filters.Count == 0)
        {
            return null;
        }

        var groups = new List<Expression>();
        foreach (var group in filters.GroupBy(f => f.FilterGroup))
        {
            var conditions = group.Select(f => Condition(f, dialect, namer, isDeny)).ToList();
            groups.Add(conditions.Count == 1
                ? conditions[0]
                : new ParenthesizedExpression(conditions.Aggregate((l, r) => new BinaryExpression(l, BinaryOperator.And, r))));
        }

        return OrChain(groups);
    }

    private static Expression Condition(ConsentRowFilter filter, TargetSqlDialect? dialect, PolicyParameterNamer namer, bool isDeny)
    {
        if (filter.FilterType == RowFilterType.SubqueryCorrelated)
        {
            return AdvancedRlsPredicateBuilder.CorrelatedExpression(filter, namer);
        }

        if (filter.FilterType == RowFilterType.CrossSourceSetFilter)
        {
            return AdvancedRlsPredicateBuilder.CrossSourceSetExpression(filter, dialect ?? TargetSqlDialect.Ansi, namer);
        }

        if (string.Equals(filter.ValueSource, "USER_ATTRIBUTE", StringComparison.OrdinalIgnoreCase))
        {
            // RR-L4-01 polarity: ALLOW matches no rows (1 = 0); DENY matches all rows (1 = 1) so NOT (1 = 1) blocks access.
            return Constant(isDeny);
        }

        var column = filter.ColumnName;
        if (!SafeIdentifierRegex().IsMatch(column))
        {
            throw new InvalidOperationException($"Invalid column name in row filter: '{column}'. Potential injection attack.");
        }

        var op = filter.Operator.ToUpperInvariant();
        if (!AllowedOperators.Contains(op))
        {
            throw new InvalidOperationException($"Unsupported operator '{filter.Operator}' in row filter.");
        }

        var columnRef = PolicyParameterNamer.Column(column);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(filter.ValueJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ValueJson in row filter must be valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (op == "IN")
            {
                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("ValueJson for the IN operator must be a valid JSON array.");
                }

                var values = root.EnumerateArray().Select(PolicyParameterNamer.ToPolicyValue).ToList();
                return values.Count == 0
                    ? Constant(isDeny)
                    : AdvancedRlsPredicateBuilder.InLists(columnRef, values, dialect is { } d ? PolicyParameterNamer.InListLimit(d) : null, namer);
            }

            if (root.ValueKind == JsonValueKind.Null)
            {
                return op switch
                {
                    "EQ" => new UnaryExpression(UnaryOperator.IsNull, columnRef),
                    "NEQ" => new UnaryExpression(UnaryOperator.IsNotNull, columnRef),
                    _ => throw new InvalidOperationException($"Operator '{op}' cannot be compared with NULL.")
                };
            }

            var parameter = namer.Add(PolicyParameterNamer.ToPolicyValue(root), isLikePattern: op == "LIKE");
            return op switch
            {
                "NEQ" => new BinaryExpression(columnRef, BinaryOperator.NotEqual, parameter),
                "LT" => new BinaryExpression(columnRef, BinaryOperator.LessThan, parameter),
                "GT" => new BinaryExpression(columnRef, BinaryOperator.GreaterThan, parameter),
                "LTE" => new BinaryExpression(columnRef, BinaryOperator.LessThanOrEqual, parameter),
                "GTE" => new BinaryExpression(columnRef, BinaryOperator.GreaterThanOrEqual, parameter),
                "LIKE" => new LikeExpression(columnRef, parameter),
                _ => new BinaryExpression(columnRef, BinaryOperator.Equal, parameter)
            };
        }
    }

    /// <summary><c>1 = 1</c> (true) or <c>1 = 0</c> (false).</summary>
    private static Expression Constant(bool value) => new BinaryExpression(
        new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(value ? 1L : 0L, LiteralType.Integer));
}
