using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

public sealed class RowFilterDisclosureTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly IReadOnlyDictionary<string, ColumnAccessLevel> Cols = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear };

    private static TableAccessDecision Shape(string shape) => shape switch
    {
        "attribute" => TableAccessDecision.Allowed(Orders, Cols, rowFilterSql: "1 = 0") with
        {
            ConsentFilterRecipe = new ConsentRowFilterRecipe(
                [[new ConsentRowFilter { ColumnName = "cost_center", Operator = "IN", ValueSource = "USER_ATTRIBUTE", UserAttribute = "secret_attribute_name" }]], [])
        },
        "sql" => TableAccessDecision.Allowed(Orders, Cols, rowFilterSql: "cost_center = 'x'"),
        "virtual-only" => TableAccessDecision.Allowed(Orders, Cols).WithMandatoryPredicate("region = 'virtual'", ["vf"]),
        _ => TableAccessDecision.Allowed(Orders, Cols)
    };

    [Theory]
    [InlineData("attribute", EffectiveAccessExplanationService.AttributeRowFilterDescription)]
    [InlineData("sql", EffectiveAccessExplanationService.RowFilterDescription)]
    [InlineData("virtual-only", EffectiveAccessExplanationService.RowFilterDescription)]
    [InlineData("none", null)]
    [Trait("Category", "Security")]
    public void MatchesDescribeMyAccess_ForEveryDecisionShape(string shape, string? expected)
    {
        var described = RowFilterDisclosure.Describe(Shape(shape));

        if (expected is null)
        {
            described.ShouldBeEmpty();
        }
        else
        {
            described.ShouldBe([expected]);
        }
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("sql")]
    [Trait("Category", "Security")]
    public void OwnerAndAdmin_NeverGetSql(string shape)
    {
        // Describe has no notion of caller: owners, admins and read-only tokens all get the same constants.
        var text = string.Join(' ', RowFilterDisclosure.Describe(Shape(shape)));

        text.ShouldNotContain("cost_center");
        text.ShouldNotContain("1 = 0");
        text.ShouldNotContain("secret_attribute_name");
    }
}
