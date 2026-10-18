using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

/// <summary>#112 WP-1: the typed filter tree becomes one parameterized Trino clause (SEC-112-02, F-7, F-8).</summary>
public sealed class TreeFilterClauseBuilderTests
{
    private const string Canary = "ZZ-CANARY-9";

    private static readonly TableMetadata Meta = new()
    {
        Columns =
        [
            new TableColumn { ColumnName = "Name", DataType = "varchar" },
            new TableColumn { ColumnName = "Age", DataType = "int" },
            new TableColumn { ColumnName = "Active", DataType = "bit" }
        ]
    };

    private static ColumnRef? Resolve(string name) => name.ToLowerInvariant() switch
    {
        "name" => new ColumnRef("Name", ColumnValueKind.String),
        "age" => new ColumnRef("Age", ColumnValueKind.Int),
        "active" => new ColumnRef("Active", ColumnValueKind.Boolean),
        _ => null
    };

    private static TableFilterClause Build(TreeFilter filter) => TreeFilterClauseBuilder.Build(filter, Resolve);

    private static List<IReadOnlyDictionary<string, object?>> Rows() =>
    [
        new Dictionary<string, object?> { ["Name"] = "alpha", ["Age"] = 10, ["Active"] = true },
        new Dictionary<string, object?> { ["Name"] = "beta_x", ["Age"] = 20, ["Active"] = false },
        new Dictionary<string, object?> { ["Name"] = "100%", ["Age"] = 30, ["Active"] = true },
        new Dictionary<string, object?> { ["Name"] = null, ["Age"] = 40, ["Active"] = false }
    ];

    public static TheoryData<TreeFilter> AllOperators() => new()
    {
        new TreeComparison("Name", TreeFilterOperator.Eq, Canary),
        new TreeComparison("Name", TreeFilterOperator.Neq, Canary),
        new TreeComparison("Age", TreeFilterOperator.Gt, 7),
        new TreeComparison("Age", TreeFilterOperator.Gte, 7),
        new TreeComparison("Age", TreeFilterOperator.Lt, 7),
        new TreeComparison("Age", TreeFilterOperator.Lte, 7),
        new TreeComparison("Name", TreeFilterOperator.In, new List<object?> { Canary, "b" }),
        new TreeComparison("Name", TreeFilterOperator.NotIn, new List<object?> { Canary, "b" }),
        new TreeComparison("Name", TreeFilterOperator.Contains, Canary),
        new TreeComparison("Name", TreeFilterOperator.StartsWith, Canary),
        new TreeComparison("Name", TreeFilterOperator.EndsWith, Canary),
        new TreeComparison("Name", TreeFilterOperator.IsNull, true)
    };

    [Theory]
    [MemberData(nameof(AllOperators))]
    public void PredicateText_NeverContainsLiteralValue_AndValuesAreParameters(TreeFilter filter)
    {
        var clause = Build(filter);

        clause.SqlPredicate.ShouldNotContain(Canary);
        clause.GetTrinoPredicate().ShouldNotContain(Canary);
        clause.GetTrinoPredicate().ShouldNotContain("'7'");
        foreach (var name in clause.Parameters.Keys)
        {
            name.ShouldStartWith("@");
            clause.SqlPredicate.ShouldContain(name);
        }
    }

    [Fact]
    public void SqlPredicate_EqualsTrinoPredicate_WithIdentityResolver()
    {
        var clause = Build(new TreeComparison("name", TreeFilterOperator.Eq, "a"));

        clause.SqlPredicate.ShouldBe(clause.GetTrinoPredicate());
        clause.SqlPredicate.ShouldStartWith("\"Name\" = @");
    }

    [Fact]
    public void ReferencedColumns_AreExactlyTheColumnsInTheTree_InCatalogSpelling()
    {
        var clause = Build(new TreeAndFilter(
        [
            new TreeComparison("name", TreeFilterOperator.Eq, "a"),
            new TreeOrFilter([new TreeComparison("AGE", TreeFilterOperator.Gt, 1), new TreeComparison("age", TreeFilterOperator.Lt, 9)]),
            new TreeNotFilter(new TreeComparison("active", TreeFilterOperator.Eq, true))
        ]));

        clause.ReferencedColumns.OrderBy(c => c).ShouldBe(["Active", "Age", "Name"]);
    }

    [Fact]
    public void TrinoFactory_UsesTheResolverSpelling()
    {
        var clause = Build(new TreeComparison("name", TreeFilterOperator.Eq, "a"));

        clause.GetTrinoPredicate(c => c.ToUpperInvariant()).ShouldStartWith("\"NAME\" = @");
    }

    [Fact]
    public void Like_UsesBangEscape_AndEscapesWildcards()
    {
        var clause = Build(new TreeComparison("name", TreeFilterOperator.Contains, "50%_!"));

        clause.SqlPredicate.ShouldContain("ESCAPE '!'");
        clause.Parameters.Values.Single().ShouldBe("%50!%!_!!%");
    }

    [Fact]
    public void StartsWithAndEndsWith_PlacePercentCorrectly()
    {
        Build(new TreeComparison("name", TreeFilterOperator.StartsWith, "a")).Parameters.Values.Single().ShouldBe("a%");
        Build(new TreeComparison("name", TreeFilterOperator.EndsWith, "a")).Parameters.Values.Single().ShouldBe("%a");
    }

    [Fact]
    public void EmptyLists_AndEmptyOr_RenderConstantPredicates()
    {
        Build(new TreeComparison("name", TreeFilterOperator.In, new List<object?>())).SqlPredicate.ShouldBe("1 = 0");
        Build(new TreeComparison("name", TreeFilterOperator.NotIn, new List<object?>())).SqlPredicate.ShouldBe("1 = 1");
        Build(new TreeOrFilter([])).SqlPredicate.ShouldBe("1 = 0");
        Build(new TreeAndFilter([])).SqlPredicate.ShouldBe("1 = 1");
    }

    [Fact]
    public void NullEquality_BecomesIsNull_WithoutParameter()
    {
        var clause = Build(new TreeComparison("name", TreeFilterOperator.Eq, null));

        clause.SqlPredicate.ShouldBe("\"Name\" IS NULL");
        clause.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void InList_OverLimit_Rejected()
    {
        var list = Enumerable.Range(0, TreeSqlCompiler.MaxInListSize + 1).Select(i => (object?)i).ToList();

        Should.Throw<GatewayInvalidQueryException>(() => Build(new TreeComparison("age", TreeFilterOperator.In, list)));
    }

    [Fact]
    public void Depth_OverLimit_Rejected()
    {
        TreeFilter filter = new TreeComparison("age", TreeFilterOperator.Gt, 1);
        for (var i = 0; i <= TreeSqlCompiler.MaxFilterDepth + 1; i++)
        {
            filter = new TreeNotFilter(filter);
        }

        Should.Throw<GatewayInvalidQueryException>(() => Build(filter));
    }

    [Fact]
    public void UnknownColumn_ThrowsUniformColumnUnavailable_WithoutName()
    {
        var ex = Should.Throw<AggregationColumnUnavailableException>(
            () => Build(new TreeComparison("secret_col", TreeFilterOperator.Eq, "a")));

        ex.Message.ShouldNotContain("secret_col");
    }

    [Fact]
    public void RelationalOperatorWithoutValue_Rejected()
    {
        Should.Throw<GatewayInvalidQueryException>(() => Build(new TreeComparison("age", TreeFilterOperator.Gt, null)));
    }

    [Fact]
    public void ValueIsCoercedToTheColumnKind()
    {
        var clause = Build(new TreeComparison("age", TreeFilterOperator.Eq, 5L));

        clause.Parameters.Values.Single().ShouldBeOfType<int>().ShouldBe(5);
    }

    [Fact]
    public void ValueNotMatchingKind_RejectedWithoutEchoingTheValue()
    {
        var ex = Should.Throw<GatewayInvalidQueryException>(
            () => Build(new TreeComparison("age", TreeFilterOperator.Eq, "not-a-number-ZZ")));

        ex.Message.ShouldNotContain("not-a-number-ZZ");
    }

    // F-7 / A-3: every operator's SqlPredicate must evaluate in the in-memory evaluator (executor branch).
    [Theory]
    [InlineData("Name", TreeFilterOperator.Eq, "alpha", new[] { "alpha" })]
    [InlineData("Age", TreeFilterOperator.Gt, 15, new[] { "beta_x", "100%", null })]
    [InlineData("Age", TreeFilterOperator.Lte, 20, new[] { "alpha", "beta_x" })]
    [InlineData("Name", TreeFilterOperator.StartsWith, "be", new[] { "beta_x" })]
    [InlineData("Name", TreeFilterOperator.Contains, "ta", new[] { "beta_x" })]
    [InlineData("Name", TreeFilterOperator.EndsWith, "x", new[] { "beta_x" })]
    [InlineData("Name", TreeFilterOperator.IsNull, true, new string?[] { null })]
    public void SqlPredicate_EvaluatesInTheInMemoryEvaluator(string column, TreeFilterOperator op, object value, string?[] expectedNames)
    {
        var clause = Build(new TreeComparison(column, op, value));

        var result = InMemoryRowFilterEvaluator.FilterRows(Rows(), clause.SqlPredicate, Meta, clause.Parameters);

        result.Select(r => (string?)r["Name"]).ShouldBe(expectedNames, ignoreOrder: true);
    }

    // A pattern that needed escaping keeps the ESCAPE clause, which the in-memory evaluator cannot read: it denies (fail closed) instead of mis-evaluating.
    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("!")]
    public void SqlPredicate_EscapedPattern_FailsClosedInTheInMemoryEvaluator(string text)
    {
        var clause = Build(new TreeComparison("name", TreeFilterOperator.Contains, text));

        InMemoryRowFilterEvaluator.FilterRows(Rows(), clause.SqlPredicate, Meta, clause.Parameters).ShouldBeEmpty();
        clause.GetTrinoPredicate().ShouldContain("ESCAPE '!'");
    }

    [Fact]
    public void TrinoPredicate_AlwaysCarriesTheEscapeClause()
    {
        var clause = Build(new TreeComparison("name", TreeFilterOperator.Contains, "plain"));

        clause.SqlPredicate.ShouldNotContain("ESCAPE");
        clause.GetTrinoPredicate().ShouldContain("ESCAPE '!'");
    }

    [Fact]
    public void SqlPredicate_InAndNotIn_EvaluateInTheInMemoryEvaluator()
    {
        var inClause = Build(new TreeComparison("name", TreeFilterOperator.In, new List<object?> { "alpha", "100%" }));
        var ninClause = Build(new TreeComparison("name", TreeFilterOperator.NotIn, new List<object?> { "alpha", "100%" }));

        InMemoryRowFilterEvaluator.FilterRows(Rows(), inClause.SqlPredicate, Meta, inClause.Parameters)
            .Select(r => (string?)r["Name"]).ShouldBe(["alpha", "100%"], ignoreOrder: true);
        InMemoryRowFilterEvaluator.FilterRows(Rows(), ninClause.SqlPredicate, Meta, ninClause.Parameters)
            .Select(r => (string?)r["Name"]).ShouldBe(["beta_x"], ignoreOrder: true);
    }
}
