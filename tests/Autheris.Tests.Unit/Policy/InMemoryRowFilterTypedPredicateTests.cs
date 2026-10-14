using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-ARC-B-04: the in-memory row filter evaluates the typed predicate (SQL three-valued logic), never re-parsed SQL text.</summary>
[Trait("Category", "Security")]
public sealed class InMemoryRowFilterTypedPredicateTests
{
    private static readonly TableIdentifier Table = new("erp", "main", "orders");

    private static readonly TableMetadata Meta = new()
    {
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" }
        ]
    };

    private static List<IReadOnlyDictionary<string, object?>> Rows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU" },
        new Dictionary<string, object?> { ["id"] = 2, ["region"] = "US" },
        new Dictionary<string, object?> { ["id"] = 3, ["region"] = null },
        new Dictionary<string, object?> { ["id"] = 4, ["region"] = "EU-West" }
    ];

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(name));

    private static LiteralExpression Str(string value) => new(value, LiteralType.String);

    private static LiteralExpression Int(long value) => new(value, LiteralType.Integer);

    private static PolicyParameterExpression Param(string name, SqlParameterType type = SqlParameterType.String, bool like = false) =>
        new(name, type, ParameterOrigin.Policy, like);

    private static PolicyPredicate Predicate(Expression expression, params (string Name, object? Value, SqlParameterType Type)[] values) =>
        PolicyPredicate.Create(expression, values.ToDictionary(v => v.Name, v => new PolicyValue(v.Value, v.Type)));

    private static IRowPolicy Policy(PolicyPredicate predicate) =>
        new PolicyPredicateRowPolicy(predicate, TargetSqlDialect.PostgreSql, new PolicyPredicateRenderer());

    private static BinaryExpression Binary(Expression left, BinaryOperator op, Expression right) => new(left, op, right);

    private static object?[] Ids(IEnumerable<IReadOnlyDictionary<string, object?>> rows) => rows.Select(r => r["id"]).ToArray();

    [Fact]
    public void EqualsParameter_KeepsOnlyMatchingRows()
    {
        var policy = Policy(Predicate(Binary(Col("region"), BinaryOperator.Equal, Param("p1")), ("p1", "EU", SqlParameterType.String)));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 1 });
    }

    [Fact]
    public void UnknownPredicateNode_DeniesAllRows()
    {
        var unsupported = new FunctionCallExpression(new SqlQualifiedName("upper"), [Col("region")]);
        var policy = Policy(Predicate(Binary(unsupported, BinaryOperator.Equal, Str("EU"))));

        InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta).ShouldBeEmpty();
    }

    [Fact]
    public void UnknownNodeInAShortCircuitedBranch_StillDeniesAllRows()
    {
        var unsupported = new FunctionCallExpression(new SqlQualifiedName("upper"), [Col("region")]);
        var expression = Binary(
            Binary(Col("id"), BinaryOperator.GreaterThanOrEqual, Int(0)),
            BinaryOperator.Or,
            Binary(unsupported, BinaryOperator.Equal, Str("EU")));

        InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(Predicate(expression)), Meta).ShouldBeEmpty();
    }

    [Fact]
    public void ColumnUnknownToTheMetadata_DeniesAllRows()
    {
        var policy = Policy(Predicate(Binary(Col("secret"), BinaryOperator.Equal, Str("x"))));

        InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta).ShouldBeEmpty();
    }

    [Fact]
    public void NullColumn_IsUnknown_AndMatchesNeitherEqualsNorNotEquals()
    {
        var notEu = Policy(Predicate(Binary(Col("region"), BinaryOperator.NotEqual, Str("EU"))));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), notEu, Meta)).ShouldBe(new object?[] { 2, 4 });
    }

    [Fact]
    public void IsNull_MatchesTheNullRowOnly()
    {
        var policy = Policy(Predicate(new UnaryExpression(UnaryOperator.IsNull, Col("region"))));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 3 });
    }

    [Fact]
    public void NotOfUnknown_IsUnknown_SoTheNullRowStaysOut()
    {
        var policy = Policy(Predicate(new UnaryExpression(UnaryOperator.Not, Binary(Col("region"), BinaryOperator.Equal, Str("EU")))));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 2, 4 });
    }

    [Fact]
    public void OrWithUnknown_UsesThreeValuedLogic()
    {
        var expression = Binary(
            Binary(Col("region"), BinaryOperator.Equal, Str("US")),
            BinaryOperator.Or,
            Binary(Col("id"), BinaryOperator.Equal, Int(3)));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(Predicate(expression)), Meta)).ShouldBe(new object?[] { 2, 3 });
    }

    [Fact]
    public void ComparisonOfDifferentKinds_DeniesAllRows_NoImplicitCoercion()
    {
        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(Predicate(Binary(Col("id"), BinaryOperator.Equal, Str("1")))), Meta))
            .ShouldBeEmpty();
        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(Predicate(Binary(Col("region"), BinaryOperator.Equal, Int(1)))), Meta))
            .ShouldBeEmpty();
    }

    [Fact]
    public void NumericParameter_ComparesAcrossIntegerWidths()
    {
        var policy = Policy(Predicate(Binary(Col("id"), BinaryOperator.Equal, Param("p1", SqlParameterType.Int64)), ("p1", 2L, SqlParameterType.Int64)));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 2 });
    }

    [Fact]
    public void InList_WithAndWithoutNegation()
    {
        var inList = new InListExpression(Col("region"), [Str("EU"), Str("US")], false);
        var notIn = new InListExpression(Col("region"), [Str("EU"), Str("US")], true);

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(Predicate(inList)), Meta)).ShouldBe(new object?[] { 1, 2 });
        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(Predicate(notIn)), Meta)).ShouldBe(new object?[] { 4 });
    }

    [Fact]
    public void Between_IsInclusive()
    {
        var policy = Policy(Predicate(new BetweenExpression(Col("id"), Int(2), Int(3), false)));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 2, 3 });
    }

    [Fact]
    public void LikeLiteralPattern_KeepsItsWildcards()
    {
        var policy = Policy(Predicate(new LikeExpression(Col("region"), Str("EU%"))));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 1, 4 });
    }

    [Fact]
    public void LikePolicyParameter_WithoutThePatternFlag_TreatsWildcardsAsText()
    {
        var policy = Policy(Predicate(new LikeExpression(Col("region"), Param("p1")), ("p1", "%", SqlParameterType.String)));

        InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta).ShouldBeEmpty();
    }

    [Fact]
    public void LikePolicyParameter_WithThePatternFlag_KeepsWildcards()
    {
        var policy = Policy(Predicate(new LikeExpression(Col("region"), Param("p1", like: true)), ("p1", "EU%", SqlParameterType.String)));

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), policy, Meta)).ShouldBe(new object?[] { 1, 4 });
    }

    [Fact]
    public void RowWithoutAReferencedColumn_IsDeniedIndividually()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["id"] = 2, ["region"] = "EU" }
        };
        var policy = Policy(Predicate(new UnaryExpression(UnaryOperator.IsNull, Col("region"))));

        InMemoryRowFilterEvaluator.FilterRows(rows, policy, Meta).ShouldBeEmpty();
    }

    [Fact]
    public void DenyAllPolicy_ReturnsNoRows()
    {
        InMemoryRowFilterEvaluator.FilterRows(Rows(), Policy(PolicyPredicate.DenyAll), Meta).ShouldBeEmpty();
    }

    [Fact]
    public void Decision_WithTypedPolicy_IsEvaluatedFromThePredicate()
    {
        var policy = Policy(Predicate(Binary(Col("region"), BinaryOperator.Equal, Param("p1")), ("p1", "US", SqlParameterType.String)));
        var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(policy);

        decision.RowPolicy.ShouldNotBeNull();
        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), decision, Meta)).ShouldBe(new object?[] { 2 });
    }

    [Fact]
    public void Decision_WhoseTextDriftedFromTheTypedPolicy_DeniesAllRows()
    {
        var policy = Policy(Predicate(Binary(Col("region"), BinaryOperator.Equal, Param("p1")), ("p1", "US", SqlParameterType.String)));
        var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(policy) with
        {
            CombinedRowFilterSql = "1 = 1"
        };

        InMemoryRowFilterEvaluator.FilterRows(Rows(), decision, Meta).ShouldBeEmpty();
    }

    [Fact]
    public void Decision_WithoutAnyRowFilter_ReturnsAllRows()
    {
        var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        InMemoryRowFilterEvaluator.FilterRows(Rows(), decision, Meta).Count.ShouldBe(4);
    }

    [Fact]
    public void Decision_WithStringOnlyFilter_StillUsesTheLegacyTextPath()
    {
        var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), "region = 'EU'", true);

        Ids(InMemoryRowFilterEvaluator.FilterRows(Rows(), decision, Meta)).ShouldBe(new object?[] { 1 });
    }
}
