using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>PLAN-GOV-129 tests 1, 15, 16, 18 (A-3, A-6): the consent leaf binds resolved caller attributes as typed policy parameters; every unresolved state is the constant.</summary>
[Trait("Category", "Security")]
public sealed class TypedRowFilterBuilderAttributeTests
{
    private static readonly TableIdentity Orders = new("main", "orders");
    private readonly TypedRowFilterBuilder _builder = new();
    private readonly PolicyPredicateRenderer _renderer = new();

    private static ConsentRowFilter Attr(string column = "cost_center", string op = "EQ", string attribute = "cost_center", int group = 0) => new()
    {
        ColumnName = column,
        Operator = op,
        ValueJson = "[]",
        ValueSource = "USER_ATTRIBUTE",
        UserAttribute = attribute,
        FilterGroup = group
    };

    private static CallerAttributeSet Set(string name, params object[] values) => new(new Dictionary<string, CallerAttributeResult>
    {
        [name] = CallerAttributeResult.Resolved(new CallerAttributeValue(values, values[0] switch { long => SqlParameterType.Int64, bool => SqlParameterType.Boolean, decimal => SqlParameterType.Decimal, _ => SqlParameterType.String }))
    });

    private static CallerAttributeSet Outcome(string name, CallerAttributeResult result) => new(new Dictionary<string, CallerAttributeResult> { [name] = result });

    private string Render(PolicyPredicate predicate) => _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql;

    private PolicyPredicate Allow(CallerAttributeSet? set, params ConsentRowFilter[] filters) =>
        _builder.BuildCombined([new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Allow, RowFilters = filters }], [], Orders, null, set)!;

    private PolicyPredicate Deny(CallerAttributeSet? set, params ConsentRowFilter[] filters) =>
        _builder.BuildCombined([], [new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Deny, RowFilters = filters }], Orders, null, set)!;

    [Fact]
    public void Allow_Eq_BindsTheScalarAttribute()
    {
        var predicate = Allow(Set("cost_center", "CC100"), Attr());
        Render(predicate).ShouldBe("\"cost_center\" = @__gql_caller_cost_center__0");
        predicate.Parameters["__gql_caller_cost_center__0"].Value.ShouldBe("CC100");
    }

    [Fact]
    public void Deny_Eq_IsNegated_AndNeq_IsNotEqual()
    {
        Render(Deny(Set("cost_center", "CC100"), Attr())).ShouldBe("NOT (\"cost_center\" = @__gql_caller_cost_center__0)");
        Render(Allow(Set("cost_center", "CC100"), Attr(op: "NEQ"))).ShouldBe("\"cost_center\" <> @__gql_caller_cost_center__0");
    }

    [Fact]
    public void In_WithAList_ExpandsToPaddedParameters()
    {
        var predicate = Allow(Set("cost_center", "a", "b", "c"), Attr(op: "IN"));
        Render(predicate).ShouldBe("\"cost_center\" IN (@__gql_caller_cost_center__0, @__gql_caller_cost_center__1, @__gql_caller_cost_center__2, @__gql_caller_cost_center__3)");
        predicate.Parameters["__gql_caller_cost_center__3"].Value.ShouldBe("c");   // the last value repeats
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    [InlineData(200, 256)]
    public void Padding_UsesPowerOfTwoBuckets(int count, int expected)
    {
        var values = Enumerable.Range(0, count).Select(i => (object)$"v{i}").ToArray();
        Allow(Set("cost_center", values), Attr(op: "IN")).Parameters.Count.ShouldBe(expected);
    }

    [Fact]
    public void Padding_CanBeSwitchedOff()
    {
        var predicate = _builder.BuildCombined([new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Allow, RowFilters = [Attr(op: "IN")] }], [], Orders, null,
            Set("cost_center", "a", "b", "c"), new CallerAttributePadding(false, 256))!;
        predicate.Parameters.Count.ShouldBe(3);
    }

    [Fact]
    public void PaddingBuckets_GiveTheSameShape_ForDifferentValueSetsOfOneBucket()
    {
        var a = Allow(Set("cost_center", "a", "b", "c"), Attr(op: "IN"));
        var b = Allow(Set("cost_center", "x", "y", "z", "w"), Attr(op: "IN"));
        var c = Allow(Set("cost_center", "x", "y", "z", "w", "v"), Attr(op: "IN"));
        a.Fingerprint.ShouldBe(b.Fingerprint);
        a.Fingerprint.ShouldNotBe(c.Fingerprint);
    }

    [Fact]
    public void TypedValues_KeepTheirParameterType()
    {
        Allow(Set("level", 3L), Attr("level", attribute: "level")).Parameters["__gql_caller_level__0"].Type.ShouldBe(SqlParameterType.Int64);
        Allow(Set("flag", true), Attr("flag", attribute: "flag")).Parameters["__gql_caller_flag__0"].Type.ShouldBe(SqlParameterType.Boolean);
    }

    [Fact]
    public void EqWithAListOfSeveralValues_FallsToTheConstant()
    {
        Render(Allow(Set("cost_center", "a", "b"), Attr())).ShouldBe("1 = 0");
        Render(Deny(Set("cost_center", "a", "b"), Attr())).ShouldBe("NOT (1 = 1)");
    }

    [Fact]
    public void EqWithAListOfOneValue_Binds()
    {
        Render(Allow(Set("cost_center", "a"), Attr())).ShouldBe("\"cost_center\" = @__gql_caller_cost_center__0");
    }

    [Theory]
    [InlineData("LIKE")]
    [InlineData("LT")]
    [InlineData("GT")]
    [InlineData("GTE")]
    [InlineData("LTE")]
    [InlineData("REGEX")]
    public void OtherOperators_FallToTheConstant(string op)
    {
        Render(Allow(Set("cost_center", "a"), Attr(op: op))).ShouldBe("1 = 0");
    }

    public static IEnumerable<object[]> UnresolvedResults() =>
    [
        [CallerAttributeResult.Missing()],
        [CallerAttributeResult.Failed("SourceUnavailable")],
        [CallerAttributeResult.OverCardinality()],
        [CallerAttributeResult.Unknown()],
        [CallerAttributeResult.NoSubject()]
    ];

    [Theory]
    [MemberData(nameof(UnresolvedResults))]
    public void ST_129_Polarity_EveryUnresolvedState_IsTheConstant(CallerAttributeResult result)
    {
        var set = Outcome("cost_center", result);
        Render(Allow(set, Attr())).ShouldBe("1 = 0");           // ALLOW: no rows
        Render(Deny(set, Attr())).ShouldBe("NOT (1 = 1)");      // DENY: blocks
    }

    [Fact]
    public void EmptyList_IsMissing_InBothPolarities()
    {
        var set = Outcome("cost_center", CallerAttributeResult.Missing());
        Render(Allow(set, Attr(op: "IN"))).ShouldBe("1 = 0");
        Render(Deny(set, Attr(op: "IN"))).ShouldBe("NOT (1 = 1)");
    }

    [Fact]
    public void AttributeNotInTheSet_AndANullSet_AreTheConstant()
    {
        Render(Allow(new CallerAttributeSet(new Dictionary<string, CallerAttributeResult>()), Attr())).ShouldBe("1 = 0");
        Render(Allow(null, Attr())).ShouldBe("1 = 0");
        Render(Deny(null, Attr())).ShouldBe("NOT (1 = 1)");
    }

    [Fact]
    public void LiteralValueJson_IsNeverUsedAsAFallback()
    {
        var withLiteral = new ConsentRowFilter { ColumnName = "cost_center", Operator = "EQ", ValueJson = "\"LEAKY\"", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center" };
        var predicate = Allow(Outcome("cost_center", CallerAttributeResult.Missing()), withLiteral);
        Render(predicate).ShouldBe("1 = 0");
        predicate.Parameters.ShouldBeEmpty();
        var resolved = Allow(Set("cost_center", "REAL"), withLiteral);
        resolved.Parameters.Values.ShouldAllBe(v => !Equals(v.Value, "LEAKY"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cost")]
    [InlineData("cost__center")]
    public void InvalidAttributeName_IsTheConstant(string? name)
    {
        var filter = new ConsentRowFilter { ColumnName = "cost_center", Operator = "EQ", ValueSource = "USER_ATTRIBUTE", UserAttribute = name };
        Render(Allow(Set("cost_center", "a"), filter)).ShouldBe("1 = 0");
    }

    [Fact]
    public void ColumnNames_AreStillValidated_OnTheAttributePath()
    {
        Should.Throw<InvalidOperationException>(() => Allow(Set("cost_center", "a"), Attr(column: "a\"; DROP TABLE x;--")));
    }

    [Fact]
    public void FilterGroups_AndCombine_AttributeAndLiteralLeaves()
    {
        var literal = new ConsentRowFilter { ColumnName = "status", Operator = "EQ", ValueJson = "\"open\"" };
        var predicate = Allow(Set("cost_center", "a"), Attr(), literal);
        Render(predicate).ShouldBe("(\"cost_center\" = @__gql_caller_cost_center__0 AND \"status\" = @__gql_rls_0)");
    }

    [Fact]
    public void ST_129_18_Text_ContainsMarkersOnly_NeverAValue()
    {
        var sql = Render(Allow(Set("cost_center", "a'; DROP TABLE x;--", "b"), Attr(op: "IN")));
        sql.ShouldNotContain("DROP");
        sql.ShouldNotContain("a'");
        sql.ShouldContain("@__gql_caller_cost_center__");
    }

    [Fact]
    public void ST_129_Caller_Parameters_CarryTheReservedName_Only()
    {
        var predicate = Allow(Set("cost_center", "a", "b"), Attr(op: "IN"));
        predicate.Parameters.Keys.ShouldNotBeEmpty();
        predicate.Parameters.Keys.All(n => CallerAttributeParameterName.TryParse(n, out var _)).ShouldBeTrue();
    }

    [Fact]
    public void ParameterName_RoundTrips_AndRejectsLookalikes()
    {
        CallerAttributeParameterName.TryParse(CallerAttributeParameterName.Format("cost_center", 7), out var attribute).ShouldBeTrue();
        attribute.ShouldBe("cost_center");
        CallerAttributeParameterName.TryParse("__gql_rls_0", out _).ShouldBeFalse();
        CallerAttributeParameterName.TryParse("__gql_caller_x", out _).ShouldBeFalse();
        CallerAttributeParameterName.TryParse("__gql_caller_Bad__0", out _).ShouldBeFalse();
        CallerAttributeParameterName.IsReserved("__GQL_CALLER_x__0").ShouldBeTrue();
    }

    [Fact]
    public void NoSetSupplied_KeepsTodaysConstants_ForBothBuilders()
    {
        var filter = new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueJson = "\"x\"", ValueSource = "USER_ATTRIBUTE" };
        Render(_builder.BuildCombined([new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Allow, RowFilters = [filter] }], [], Orders)!).ShouldBe("1 = 0");
        Render(_builder.BuildCombined([], [new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Deny, RowFilters = [filter] }], Orders)!).ShouldBe("NOT (1 = 1)");
    }
}
