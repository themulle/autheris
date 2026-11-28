using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.GraphQL;
using TrinoSqlEngine.Ast.Nodes;
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
        "virtual-typed-only" => TableAccessDecision.Allowed(Orders, Cols).WithMandatoryPredicate(Typed("region"), ["vf"]),
        "typed-only" => TableAccessDecision.Allowed(Orders, Cols).WithAdditionalRowPolicy(Typed("cost_center")),
        "consent-and-virtual" => TableAccessDecision.Allowed(Orders, Cols, rowFilterSql: "cost_center = 'x'").WithMandatoryPredicate("region = 'virtual'", ["vf"]),
        "typed-consent-and-virtual" => TableAccessDecision.Allowed(Orders, Cols).WithAdditionalRowPolicy(Typed("cost_center")).WithMandatoryPredicate(Typed("region"), ["vf"]),
        "virtual-then-abac" => TableAccessDecision.Allowed(Orders, Cols).WithMandatoryPredicate("region = 'virtual'", ["vf"]).WithAdditionalRowFilter("tier = 1"),
        _ => TableAccessDecision.Allowed(Orders, Cols)
    };

    private static IRowPolicy Typed(string column) => TypedTreeRowPolicy.Text(column, BinaryOperator.Equal, "v");

    [Theory]
    [InlineData("attribute", EffectiveAccessExplanationService.AttributeRowFilterDescription)]
    [InlineData("sql", EffectiveAccessExplanationService.RowFilterDescription)]
    [InlineData("virtual-only", null)]
    [InlineData("virtual-typed-only", null)]
    [InlineData("typed-only", EffectiveAccessExplanationService.RowFilterDescription)]
    [InlineData("consent-and-virtual", EffectiveAccessExplanationService.RowFilterDescription)]
    [InlineData("typed-consent-and-virtual", EffectiveAccessExplanationService.RowFilterDescription)]
    [InlineData("virtual-then-abac", EffectiveAccessExplanationService.RowFilterDescription)]
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
