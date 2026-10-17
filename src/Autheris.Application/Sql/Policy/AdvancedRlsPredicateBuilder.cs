namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Typed producers of the advanced row filters (correlated subquery, cross-source set). They build AST nodes only: identifiers are
/// validated and quoted by the generator, and every consent value is a <see cref="PolicyParameterExpression"/> (SEC-X1-02).
/// The legacy string producers in <see cref="AdvancedRlsFilterGenerator"/> stay until X1.
/// </summary>
internal static class AdvancedRlsPredicateBuilder
{
    public static PolicyPredicate BuildCorrelated(
        ConsentRowFilter filter,
        string parameterPrefix = PolicyParameterNamer.DefaultPrefix,
        CorrelatedFilterSource source = CorrelatedFilterSource.Consent)
    {
        var namer = new PolicyParameterNamer(parameterPrefix);
        return PolicyPredicate.Create(CorrelatedExpression(filter, namer, source), namer.Values);
    }

    public static PolicyPredicate BuildCrossSourceSet(ConsentRowFilter filter, TargetSqlDialect dialect, string parameterPrefix = PolicyParameterNamer.DefaultPrefix)
    {
        var namer = new PolicyParameterNamer(parameterPrefix);
        return PolicyPredicate.Create(CrossSourceSetExpression(filter, dialect, namer), namer.Values);
    }

    /// <summary>
    /// The correlated EXISTS over the reserved target alias. Always the EXISTS form: the SQL Server IN-strategy was a text-level optimisation.
    /// Issue #107: the node is registered as a correlated filter site (side table, reference identity); this does not change the AST or the
    /// generated SQL. <paramref name="source"/> says where the rule comes from: a consent rule carries its own evaluation settings, a Casbin
    /// rule has none (object and global settings only).
    /// </summary>
    internal static Expression CorrelatedExpression(ConsentRowFilter filter, PolicyParameterNamer namer, CorrelatedFilterSource source)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.DependentTable == null)
        {
            throw new InvalidOperationException("DependentTable must not be null for SubqueryCorrelated.");
        }

        var configuredTargetAlias = string.IsNullOrWhiteSpace(filter.TargetTableAlias) ? "target" : filter.TargetTableAlias;
        var depAlias = string.IsNullOrWhiteSpace(filter.DependentTableAlias) ? "dep" : filter.DependentTableAlias;
        var targetFk = string.IsNullOrWhiteSpace(filter.ForeignKeyColumn) ? filter.ColumnName : filter.ForeignKeyColumn;
        var depPk = string.IsNullOrWhiteSpace(filter.PrimaryKeyColumn) ? "id" : filter.PrimaryKeyColumn;

        AdvancedRlsFilterGenerator.ValidateIdentifier(configuredTargetAlias, "TargetTableAlias");
        AdvancedRlsFilterGenerator.ValidateIdentifier(depAlias, "DependentTableAlias");
        AdvancedRlsFilterGenerator.ValidateIdentifier(targetFk, "ForeignKeyColumn");
        AdvancedRlsFilterGenerator.ValidateIdentifier(depPk, "PrimaryKeyColumn");

        // A dependent or hop alias equal to the target alias would turn the correlation into dep.pk = dep.fk (true for every row).
        var innerAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AdvancedRlsFilterGenerator.EnsureDistinctInnerAlias(depAlias, configuredTargetAlias, innerAliases);
        foreach (var hop in filter.AdditionalHops ?? [])
        {
            AdvancedRlsFilterGenerator.ValidateIdentifier(hop.TableAlias, "SubqueryJoinHop.TableAlias");
            AdvancedRlsFilterGenerator.EnsureDistinctInnerAlias(hop.TableAlias, configuredTargetAlias, innerAliases);
        }

        string Rebase(string qualified) => AdvancedRlsFilterGenerator.RebaseTargetQualifier(qualified, configuredTargetAlias);
        ColumnReference Col(string qualified) => PolicyParameterNamer.Column(Rebase(qualified));

        TableSource from = Table(filter.DependentTable.Value, depAlias);
        foreach (var hop in filter.AdditionalHops ?? [])
        {
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(hop.LeftJoinColumn, "SubqueryJoinHop.LeftJoinColumn");
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(hop.RightJoinColumn, "SubqueryJoinHop.RightJoinColumn");
            from = new JoinedTableSource(from, JoinType.Inner, Table(hop.Table, hop.TableAlias),
                new OnJoinCondition(new BinaryExpression(Col(hop.LeftJoinColumn), BinaryOperator.Equal, Col(hop.RightJoinColumn))));
        }

        var conditions = new List<Expression>
        {
            new BinaryExpression(
                PolicyParameterNamer.Column($"{depAlias}.{depPk}"), BinaryOperator.Equal,
                PolicyParameterNamer.Column($"{RowFilterAliases.Target}.{targetFk}"))
        };

        if (!string.IsNullOrWhiteSpace(filter.SubqueryFilterPredicateJson))
        {
            conditions.AddRange(SubqueryPredicates(filter.SubqueryFilterPredicateJson, Rebase, depAlias, namer));
        }

        bool temporal = !string.IsNullOrWhiteSpace(filter.TargetTemporalColumn) && !string.IsNullOrWhiteSpace(filter.DependentValidFromColumn);
        if (temporal)
        {
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(filter.TargetTemporalColumn!, "TargetTemporalColumn");
            AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(filter.DependentValidFromColumn!, "DependentValidFromColumn");

            ColumnReference Temporal() => filter.TargetTemporalColumn!.Contains('.')
                ? Col(filter.TargetTemporalColumn!)
                : PolicyParameterNamer.Column($"{RowFilterAliases.Target}.{filter.TargetTemporalColumn!}");
            ColumnReference Dep(string column) => column.Contains('.') ? Col(column) : PolicyParameterNamer.Column($"{depAlias}.{column}");

            conditions.Add(new BinaryExpression(Temporal(), BinaryOperator.GreaterThanOrEqual, Dep(filter.DependentValidFromColumn!)));
            if (!string.IsNullOrWhiteSpace(filter.DependentValidToColumn))
            {
                AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(filter.DependentValidToColumn, "DependentValidToColumn");
                conditions.Add(new ParenthesizedExpression(new BinaryExpression(
                    new UnaryExpression(UnaryOperator.IsNull, Dep(filter.DependentValidToColumn)),
                    BinaryOperator.Or,
                    new BinaryExpression(Temporal(), BinaryOperator.LessThan, Dep(filter.DependentValidToColumn)))));
            }
        }

        var exists = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                from, And(conditions), null, null), null, null));
        CorrelatedFilterSiteRegistry.Register(exists, Site(filter, source, temporal));
        return exists;
    }

    private static CorrelatedFilterSite Site(ConsentRowFilter filter, CorrelatedFilterSource source, bool temporal)
    {
        if (source == CorrelatedFilterSource.Casbin)
        {
            return new CorrelatedFilterSite(null, source, null, !temporal);
        }

        RowFilterEvaluationSettings? settings = filter.KeySetStrategy is null && filter.KeySetDurationSeconds is null && filter.KeySetMaxKeys is null
            ? null
            : new RowFilterEvaluationSettings(filter.KeySetStrategy, filter.KeySetDurationSeconds, filter.KeySetMaxKeys);
        return new CorrelatedFilterSite(filter.Id, source, settings, !temporal);
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

    private static IEnumerable<Expression> SubqueryPredicates(string json, Func<string, string> rebase, string defaultAlias, PolicyParameterNamer namer)
    {
        string Qualify(string column) => column.Contains('.') ? column : $"{defaultAlias}.{column}";
        var conditions = new List<Expression>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(prop.Name, "SubqueryPredicate.Column");
                    var column = PolicyParameterNamer.Column(rebase(Qualify(prop.Name)));
                    conditions.Add(prop.Value.ValueKind == JsonValueKind.Null
                        ? new UnaryExpression(UnaryOperator.IsNull, column)
                        : new BinaryExpression(column, BinaryOperator.Equal, namer.Add(PolicyParameterNamer.ToPolicyValue(prop.Value))));
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException("Subquery predicate array must contain only JSON objects.");
                    }

                    if (!elem.TryGetProperty("column", out var colElem) || colElem.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException("Subquery predicate must contain a valid 'column' property.");
                    }

                    var columnName = colElem.GetString()!;
                    AdvancedRlsFilterGenerator.ValidateQualifiedIdentifier(columnName, "SubqueryPredicate.Column");
                    var op = elem.TryGetProperty("op", out var opProp) ? opProp.GetString()?.ToUpperInvariant() ?? "EQ" : "EQ";
                    if (!AdvancedRlsFilterGenerator.AllowedSubqueryOperators.Contains(op))
                    {
                        throw new InvalidOperationException($"Unsupported operator '{op}' in subquery predicate.");
                    }

                    if (!elem.TryGetProperty("value", out var rawVal))
                    {
                        throw new InvalidOperationException("Subquery predicate must contain a 'value' property.");
                    }

                    var column = PolicyParameterNamer.Column(rebase(Qualify(columnName)));
                    if (rawVal.ValueKind == JsonValueKind.Null)
                    {
                        conditions.Add(new UnaryExpression(op == "NEQ" ? UnaryOperator.IsNotNull : UnaryOperator.IsNull, column));
                        continue;
                    }

                    var parameter = namer.Add(PolicyParameterNamer.ToPolicyValue(rawVal), isLikePattern: op == "LIKE");
                    conditions.Add(op switch
                    {
                        "NEQ" => new BinaryExpression(column, BinaryOperator.NotEqual, parameter),
                        "LT" => new BinaryExpression(column, BinaryOperator.LessThan, parameter),
                        "GT" => new BinaryExpression(column, BinaryOperator.GreaterThan, parameter),
                        "LTE" => new BinaryExpression(column, BinaryOperator.LessThanOrEqual, parameter),
                        "GTE" => new BinaryExpression(column, BinaryOperator.GreaterThanOrEqual, parameter),
                        "LIKE" => new LikeExpression(column, parameter),
                        _ => new BinaryExpression(column, BinaryOperator.Equal, parameter)
                    });
                }
            }
            else
            {
                throw new InvalidOperationException("SubqueryFilterPredicateJson must be a JSON object or JSON array.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("SubqueryFilterPredicateJson must be valid JSON.");
        }

        return conditions;
    }

    private static NamedTableSource Table(Autheris.Domain.Common.TableIdentifier table, string alias)
    {
        AdvancedRlsFilterGenerator.ValidateIdentifier(table.Schema, "Schema");
        AdvancedRlsFilterGenerator.ValidateIdentifier(table.TableName, "TableName");
        return new NamedTableSource(
            new SqlQualifiedName(new[] { new SqlIdentifier(table.Schema, true), new SqlIdentifier(table.TableName, true) }),
            new SqlIdentifier(alias, true));
    }

    private static Expression And(IReadOnlyList<Expression> parts) =>
        parts.Aggregate((left, right) => new BinaryExpression(left, BinaryOperator.And, right));
}
