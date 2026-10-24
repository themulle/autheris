namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Application.Sql.Tree;
using Shouldly;
using Xunit;

/// <summary>#328 GAP-051: three-valued logic for IN / NOT IN lists that contain NULL.</summary>
public sealed class TreeFilterRendererTests
{
    private sealed class Target : ITreeFilterTarget
    {
        public List<object?> Parameters { get; } = [];

        public string Column(string column) => "\"" + column + "\"";

        public string AddParameter(object? value)
        {
            Parameters.Add(value);
            return "@p" + (Parameters.Count - 1);
        }

        public char LikeEscape => '\\';

        public string LikeSpecials => "%_";

        public bool AlwaysEmitLikeEscape => true;
    }

    private static (string Sql, Target Target) Render(TreeFilterOperator op, params object?[] values)
    {
        var target = new Target();
        var sql = TreeFilterRenderer.Render(new TreeComparison("region", op, values.ToList()), target);
        return (sql, target);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NotInWithNull_RendersFalse()
    {
        // SEC-RG-16: x NOT IN (a, NULL) is never true in 3VL; narrower, never wider.
        var (sql, target) = Render(TreeFilterOperator.NotIn, "A", null);

        sql.ShouldBe("1 = 0");
        target.Parameters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NotInOnlyNull_RendersFalse()
    {
        Render(TreeFilterOperator.NotIn, new object?[] { null }).Sql.ShouldBe("1 = 0");
    }

    [Fact]
    public void InWithNull_MatchesNonNullOnly()
    {
        var (sql, target) = Render(TreeFilterOperator.In, "A", null);

        sql.ShouldBe("\"region\" IN (@p0)");
        target.Parameters.ShouldBe(["A"]);
    }

    [Fact]
    public void InAllNull_RendersFalse()
    {
        Render(TreeFilterOperator.In, new object?[] { null, null }).Sql.ShouldBe("1 = 0");
    }

    [Fact]
    public void NotInWithoutNull_Unchanged()
    {
        var (sql, target) = Render(TreeFilterOperator.NotIn, "A", "B");

        sql.ShouldBe("\"region\" NOT IN (@p0, @p1)");
        target.Parameters.ShouldBe(["A", "B"]);
    }

    [Fact]
    public void NotInEmptyList_RendersTrue()
    {
        Render(TreeFilterOperator.NotIn).Sql.ShouldBe("1 = 1");
    }
}
