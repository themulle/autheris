using Autheris.Application.Analytics;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

/// <summary>#112 B-2 / T-S-15: leaf and string-length caps are enforced by the builder, before any engine call.</summary>
public sealed class TreeFilterClauseBuilderCapTests
{
    private static ColumnRef? Resolve(string name) => name.ToLowerInvariant() switch
    {
        "name" => new ColumnRef("Name", ColumnValueKind.String),
        "age" => new ColumnRef("Age", ColumnValueKind.Int),
        _ => null
    };

    private static TreeFilter Leaves(int count) =>
        new TreeAndFilter(Enumerable.Range(0, count).Select(i => (TreeFilter)new TreeComparison("Age", TreeFilterOperator.Gt, i)).ToList());

    [Fact]
    public void Build_AtTheLeafCap_Succeeds()
    {
        var clause = TreeFilterClauseBuilder.Build(Leaves(TreeFilterClauseBuilder.MaxLeafCount), Resolve);

        clause.Parameters.Count.ShouldBe(TreeFilterClauseBuilder.MaxLeafCount);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Build_AboveTheLeafCap_IsRejected_B2()
    {
        Should.Throw<GatewayInvalidQueryException>(() =>
            TreeFilterClauseBuilder.Build(Leaves(TreeFilterClauseBuilder.MaxLeafCount + 1), Resolve));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Build_LeavesAreCountedAcrossOrAndNot_B2()
    {
        var half = Leaves(TreeFilterClauseBuilder.MaxLeafCount / 2 + 1);
        var tree = new TreeOrFilter([half, new TreeNotFilter(half)]);

        Should.Throw<GatewayInvalidQueryException>(() => TreeFilterClauseBuilder.Build(tree, Resolve));
    }

    [Fact]
    public void Build_StringValueAtTheCap_Succeeds()
    {
        var value = new string('a', TreeFilterClauseBuilder.MaxStringValueLength);

        TreeFilterClauseBuilder.Build(new TreeComparison("Name", TreeFilterOperator.Eq, value), Resolve)
            .Parameters.Values.ShouldContain(value);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_StringValueAboveTheCap_IsRejectedWithoutEchoingIt_B2(bool inList)
    {
        var value = "CANARY" + new string('a', TreeFilterClauseBuilder.MaxStringValueLength);
        object operand = inList ? new List<object?> { "ok", value } : value;
        var op = inList ? TreeFilterOperator.In : TreeFilterOperator.Eq;

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            TreeFilterClauseBuilder.Build(new TreeComparison("Name", op, operand), Resolve));

        ex.Message.ShouldNotContain("CANARY");
    }
}
