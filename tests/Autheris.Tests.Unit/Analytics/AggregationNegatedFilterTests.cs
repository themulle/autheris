namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>MISC-09: RestrictNegatedFilters is enforced on tokens (never regex); unparseable WHERE rejects.</summary>
[Trait("Category", "Security")]
public sealed class AggregationNegatedFilterTests
{
    private static readonly TableMetadata Meta = new()
    {
        Identifier = new TableIdentifier("hr", "public", "employees"),
        Columns =
        [
            new TableColumn { ColumnName = "employee_id", DataType = "int" },
            new TableColumn { ColumnName = "department", DataType = "varchar" },
            new TableColumn { ColumnName = "status", DataType = "varchar" },
            new TableColumn { ColumnName = "salary", DataType = "decimal" }
        ],
        PrimaryKeyColumns = ["employee_id"]
    };

    private static void Check(string sql, string[]? groupBy = null, bool restrict = true, int minGroupSize = 5) =>
        AggregationNegationGuard.Enforce(
            new TableFilterClause(sql, new Dictionary<string, object?>(), []),
            groupBy ?? ["department"],
            Meta,
            minGroupSize,
            restrict);

    private static void ShouldReject(string sql, string[]? groupBy = null) =>
        Should.Throw<AggregationFilterRejectedException>(() => Check(sql, groupBy)).ErrorCode.ShouldBe("AGG_NEGATED_FILTER_REJECTED");

    [Fact]
    public void NotEqualsOnNonGroupColumn_Rejected() => ShouldReject("employee_id <> 7");

    [Fact]
    public void BangEqualsOnNonGroupColumn_Rejected() => ShouldReject("\"status\" != 'A'");

    [Fact]
    public void NotIn_Rejected() => ShouldReject("status NOT IN ('A', 'B')");

    [Fact]
    public void NotLike_Rejected() => ShouldReject("status NOT LIKE 'A%'");

    [Fact]
    public void NotBetween_Rejected() => ShouldReject("salary NOT BETWEEN 10 AND 20");

    [Fact]
    public void NotParenthesised_Rejected() => ShouldReject("NOT (status = 'A')");

    [Fact]
    public void IsNotTrue_Rejected() => ShouldReject("status IS NOT TRUE");

    [Fact]
    public void EqualityOnPrimaryKey_Rejected() => ShouldReject("employee_id = 7");

    [Fact]
    public void NegationInsideAndedClause_Rejected() => ShouldReject("department = 'X' AND status <> 'A'");

    [Fact]
    public void NegationOnGroupByColumn_Allowed()
    {
        Check("department <> 'X'");
        Check("department NOT IN ('X', 'Y') AND salary > 10");
    }

    [Fact]
    public void IsNotNull_Allowed() => Check("status IS NOT NULL");

    [Fact]
    public void PositiveFilterOnNonGroupColumn_Allowed() => Check("status = 'A' AND salary > 10");

    [Fact]
    public void BetweenWithAnd_PositiveStaysAllowed() => Check("salary BETWEEN 10 AND 20 AND department <> 'X'");

    [Fact]
    public void OptionFalse_NegationAllowed() => Check("employee_id <> 7", restrict: false);

    [Fact]
    public void MinGroupSizeOne_NegationAllowed() => Check("employee_id <> 7", minGroupSize: 1);

    [Fact]
    public void UnparseableWhere_Rejected()
    {
        ShouldReject("status = 'unterminated");
        ShouldReject("(status = 'A'");
        ShouldReject("status = 'A')");
        ShouldReject("status = # 1");
    }

    [Fact]
    public void BoundParameterPlaceholders_AreTokenised()
    {
        Check("status = @gq1 AND salary > @gq2");
        ShouldReject("status <> @gq1");
    }

    [Fact]
    public void NegatedLiteralTextInString_DoesNotTrigger() => Check("status = 'a <> b'");
}
