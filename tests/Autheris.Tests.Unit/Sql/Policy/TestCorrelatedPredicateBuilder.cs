using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql.Policy;

internal sealed record TestCorrelatedRule
{
    public int FilterGroup { get; init; }
    public bool IsCorrelated { get; init; }
    public string? ColumnName { get; init; }
    public string Operator { get; init; } = "EQ";
    public string? ValueJson { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public TableIdentifier DependentTable { get; init; } = new("erp", "main", "customers");
    public string DependentTableAlias { get; init; } = "c";
    public string ForeignKeyColumn { get; init; } = "client_id";
    public string PrimaryKeyColumn { get; init; } = "id";
    public string? WhereColumn { get; init; }
    public object? WhereValue { get; init; }
    public (TableIdentifier Table, string Alias, string LeftColumn, string RightColumn)? Hop { get; init; }
    public string? TargetTemporalColumn { get; init; }
    public string? DependentValidFromColumn { get; init; }
    public RowFilterEvaluationStrategy? KeySetStrategy { get; init; }
    public int? KeySetDurationSeconds { get; init; }
    public int? KeySetMaxKeys { get; init; }

    public static TestCorrelatedRule Simple(string column = "region", string valueJson = "\"EU\"", int group = 0) =>
        new() { FilterGroup = group, ColumnName = column, Operator = "EQ", ValueJson = valueJson };
}

internal sealed record TestConsent(ConsentEffect Effect, IReadOnlyList<TestCorrelatedRule> RowFilters);

/// <summary>
/// Test-only helper that constructs typed EXISTS predicates registered in <see cref="CorrelatedFilterSiteRegistry"/>
/// so the key-set cache, substitution, and compiler pipeline tests exercise real AST sites without deprecated consent filter models.
/// </summary>
internal sealed class TestCorrelatedPredicateBuilder
{
    public static PolicyPredicate BuildSingle(
        string fkColumn = "client_id",
        string domain = "default",
        string schema = "public",
        string table = "customers",
        string alias = "c",
        string pkColumn = "id",
        string? whereColumn = "c.country",
        object? whereValue = null,
        bool setWhereCountryCh = false) =>
        new TestCorrelatedPredicateBuilder().Build(
        [
            new TestCorrelatedRule
            {
                IsCorrelated = true,
                DependentTable = new TableIdentifier(domain, schema, table),
                DependentTableAlias = alias,
                ForeignKeyColumn = fkColumn,
                PrimaryKeyColumn = pkColumn,
                WhereColumn = whereColumn,
                WhereValue = setWhereCountryCh ? "CH" : whereValue
            }
        ], new TableIdentity(schema, table));

    public PolicyPredicate Build(IReadOnlyList<TestCorrelatedRule> rules, TableIdentity table)
    {
        var namer = new PolicyParameterNamer();
        var expr = BuildGroupExpression(rules, namer)
            ?? throw new InvalidOperationException($"No rules produced a predicate for {table}.");
        return PolicyPredicate.Create(expr, namer.Values);
    }

    public PolicyPredicate? BuildCombined(IReadOnlyList<TestConsent> allow, IReadOnlyList<TestConsent> deny, TableIdentity table)
    {
        _ = table;
        var namer = new PolicyParameterNamer();
        Expression? allowExpr = null;
        if (!allow.Any(c => c.RowFilters.Count == 0))
        {
            allowExpr = OrChain(allow.Select(c => BuildGroupExpression(c.RowFilters, namer)));
        }

        var denyExpr = OrChain(deny.Where(c => c.RowFilters.Count > 0).Select(c => BuildGroupExpression(c.RowFilters, namer)));
        if (allowExpr is null && denyExpr is null)
        {
            return null;
        }

        var combined = (allowExpr, denyExpr) switch
        {
            ({ } a, null) => a,
            (null, { } d) => new UnaryExpression(UnaryOperator.Not, new ParenthesizedExpression(d)),
            ({ } a, { } d) => new BinaryExpression(
                new ParenthesizedExpression(a),
                BinaryOperator.And,
                new UnaryExpression(UnaryOperator.Not, new ParenthesizedExpression(d))),
            _ => throw new InvalidOperationException()
        };
        return PolicyPredicate.Create(combined, namer.Values);
    }

    private static Expression? BuildGroupExpression(IReadOnlyList<TestCorrelatedRule> rules, PolicyParameterNamer namer)
    {
        if (rules.Count == 0)
        {
            return null;
        }

        var groups = rules
            .GroupBy(static r => r.FilterGroup)
            .OrderBy(static g => g.Key)
            .Select(g => AndChain(g.Select(r => BuildRuleExpression(r, namer))));
        return OrChain(groups);
    }

    private static Expression BuildRuleExpression(TestCorrelatedRule rule, PolicyParameterNamer namer) =>
        rule.IsCorrelated ? BuildExists(rule, namer) : BuildSimple(rule, namer);

    private static Expression BuildSimple(TestCorrelatedRule rule, PolicyParameterNamer namer)
    {
        var col = PolicyParameterNamer.Column(rule.ColumnName ?? throw new InvalidOperationException("ColumnName required."));
        using var doc = JsonDocument.Parse(rule.ValueJson ?? "null");
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new UnaryExpression(UnaryOperator.IsNull, col);
        }

        var val = root.ValueKind switch
        {
            JsonValueKind.Number when root.TryGetInt64(out var l) => new PolicyValue(l, SqlParameterType.Int64),
            JsonValueKind.True or JsonValueKind.False => new PolicyValue(root.GetBoolean(), SqlParameterType.Boolean),
            JsonValueKind.String => new PolicyValue(root.GetString(), SqlParameterType.String),
            _ => new PolicyValue(root.GetRawText(), SqlParameterType.String)
        };
        return new BinaryExpression(col, BinaryOperator.Equal, namer.Add(val));
    }

    private static ExistsExpression BuildExists(TestCorrelatedRule rule, PolicyParameterNamer namer)
    {
        TableSource from = QualifiedTable(rule.DependentTable, rule.DependentTableAlias);
        if (rule.Hop is { } hop)
        {
            from = new JoinedTableSource(
                from,
                JoinType.Inner,
                QualifiedTable(hop.Table, hop.Alias),
                new OnJoinCondition(new BinaryExpression(
                    PolicyParameterNamer.Column(hop.LeftColumn),
                    BinaryOperator.Equal,
                    PolicyParameterNamer.Column(hop.RightColumn))));
        }

        var conditions = new List<Expression>
        {
            new BinaryExpression(
                PolicyParameterNamer.Column($"{rule.DependentTableAlias}.{rule.PrimaryKeyColumn}"),
                BinaryOperator.Equal,
                PolicyParameterNamer.Column($"{RowFilterAliases.Target}.{rule.ForeignKeyColumn}"))
        };

        if (rule.WhereColumn is not null)
        {
            var col = PolicyParameterNamer.Column(rule.WhereColumn);
            conditions.Add(rule.WhereValue switch
            {
                null => new UnaryExpression(UnaryOperator.IsNull, col),
                string s => new BinaryExpression(col, BinaryOperator.Equal, namer.Add(new PolicyValue(s, SqlParameterType.String))),
                long l => new BinaryExpression(col, BinaryOperator.Equal, namer.Add(new PolicyValue(l, SqlParameterType.Int64))),
                int i => new BinaryExpression(col, BinaryOperator.Equal, namer.Add(new PolicyValue((long)i, SqlParameterType.Int64))),
                bool b => new BinaryExpression(col, BinaryOperator.Equal, namer.Add(new PolicyValue(b, SqlParameterType.Boolean))),
                _ => new BinaryExpression(col, BinaryOperator.Equal, namer.Add(new PolicyValue(Convert.ToString(rule.WhereValue, CultureInfo.InvariantCulture), SqlParameterType.String)))
            });
        }

        if (rule.TargetTemporalColumn is not null && rule.DependentValidFromColumn is not null)
        {
            conditions.Add(new BinaryExpression(
                PolicyParameterNamer.Column($"{RowFilterAliases.Target}.{rule.TargetTemporalColumn}"),
                BinaryOperator.GreaterThanOrEqual,
                PolicyParameterNamer.Column($"{rule.DependentTableAlias}.{rule.DependentValidFromColumn}")));
        }

        var exists = new ExistsExpression(new SelectStatement(
            null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
                from,
                AndChain(conditions),
                null,
                null),
            null,
            null));

        RowFilterEvaluationSettings? settings = rule.KeySetStrategy.HasValue || rule.KeySetDurationSeconds.HasValue || rule.KeySetMaxKeys.HasValue
            ? new RowFilterEvaluationSettings(rule.KeySetStrategy, rule.KeySetDurationSeconds, rule.KeySetMaxKeys)
            : null;
        bool eligible = rule.TargetTemporalColumn is null && rule.DependentValidFromColumn is null;
        CorrelatedFilterSiteRegistry.Register(exists, new CorrelatedFilterSite(rule.Id, CorrelatedFilterSource.Consent, settings, eligible));
        return exists;
    }

    private static NamedTableSource QualifiedTable(TableIdentifier table, string alias)
    {
        var name = string.IsNullOrWhiteSpace(table.Schema)
            ? new SqlQualifiedName(new[] { new SqlIdentifier(table.TableName, true) })
            : new SqlQualifiedName(new[] { new SqlIdentifier(table.Schema, true), new SqlIdentifier(table.TableName, true) });
        return new NamedTableSource(name, new SqlIdentifier(alias, true));
    }

    private static Expression? AndChain(IEnumerable<Expression?> parts)
    {
        Expression? current = null;
        foreach (var part in parts.Where(static p => p is not null))
        {
            current = current is null ? part : new BinaryExpression(current, BinaryOperator.And, part!);
        }

        return current;
    }

    private static Expression? OrChain(IEnumerable<Expression?> parts)
    {
        var list = parts.Where(static p => p is not null).Cast<Expression>().ToList();
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count == 1)
        {
            return list[0];
        }

        var current = list[0];
        for (int i = 1; i < list.Count; i++)
        {
            current = new BinaryExpression(current, BinaryOperator.Or, list[i]);
        }

        return new ParenthesizedExpression(current);
    }
}
