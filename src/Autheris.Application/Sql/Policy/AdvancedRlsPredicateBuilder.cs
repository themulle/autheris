namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Typed producer of cross-source set row filters. Builds AST nodes only: identifiers are
/// validated and quoted by the generator, and every consent value is a <see cref="PolicyParameterExpression"/>.
/// </summary>
internal static class AdvancedRlsPredicateBuilder
{
    public static PolicyPredicate BuildCrossSourceSet(ConsentRowFilter filter, TargetSqlDialect dialect, string parameterPrefix = PolicyParameterNamer.DefaultPrefix)
    {
        var namer = new PolicyParameterNamer(parameterPrefix);
        return PolicyPredicate.Create(CrossSourceSetExpression(filter, dialect, namer), namer.Values);
    }

    /// <summary>The set filter: one IN list with one parameter per value (null elements skipped), chunked only at the dialect IN-list limit.</summary>
    internal static Expression CrossSourceSetExpression(ConsentRowFilter filter, TargetSqlDialect dialect, PolicyParameterNamer namer)
    {
        ArgumentNullException.ThrowIfNull(filter);
        AdvancedRlsFilterGenerator.ValidateIdentifier(filter.ColumnName, "ColumnName");

        var values = new List<PolicyValue>();
        if (!string.IsNullOrWhiteSpace(filter.ValueJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(filter.ValueJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    values.AddRange(doc.RootElement.EnumerateArray()
                        .Where(e => e.ValueKind != JsonValueKind.Null).Select(PolicyParameterNamer.ToPolicyValue));
                }
                else if (doc.RootElement.ValueKind != JsonValueKind.Null)
                {
                    values.Add(PolicyParameterNamer.ToPolicyValue(doc.RootElement));
                }
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("ValueJson in CrossSourceSetFilter must be valid JSON.");
            }
        }

        if (values.Count == 0)
        {
            return PolicyPredicate.DenyAll.Expression;
        }

        return InLists(PolicyParameterNamer.Column(filter.ColumnName), values, PolicyParameterNamer.InListLimit(dialect), namer);
    }

    /// <summary><c>col IN (p0, p1, ...)</c>; above <paramref name="limit"/> items several lists joined by OR (ORA-01795).</summary>
    internal static Expression InLists(ColumnReference column, IReadOnlyList<PolicyValue> values, int? limit, PolicyParameterNamer namer)
    {
        int size = limit is > 0 ? limit.Value : Math.Max(values.Count, 1);
        var lists = new List<Expression>();
        foreach (var chunk in values.Chunk(size))
        {
            lists.Add(new InListExpression(column, chunk.Select(v => (Expression)namer.Add(v)).ToList(), false));
        }

        return lists.Count == 1
            ? lists[0]
            : new ParenthesizedExpression(lists.Aggregate((left, right) => new BinaryExpression(left, BinaryOperator.Or, right)));
    }
}
