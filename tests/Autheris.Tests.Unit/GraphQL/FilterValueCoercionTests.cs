using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#299 GAP-008 / #303 GAP-012: a catalog <c>date</c> column compares as a calendar date, a timestamp column as a UTC instant.</summary>
public sealed class FilterValueCoercionTests
{
    [Fact]
    public void DateColumn_DateOnlyLiteral_CoercesToDateOnly()
    {
        FilterValueCoercion.Coerce("2024-02-29", ColumnValueKind.DateTime, TreeFilterOperator.Eq, dateOnly: true)
            .ShouldBe(new DateOnly(2024, 2, 29));
        FilterValueCoercion.Coerce("2024-03-01", ColumnValueKind.DateTime, TreeFilterOperator.Lt, dateOnly: true)
            .ShouldBe(new DateOnly(2024, 3, 1));
    }

    [Fact]
    public void DateColumn_InList_CoercesEveryItemToDateOnly()
    {
        var list = FilterValueCoercion.Coerce(new List<object?> { "2024-02-29", "2024-03-01" }, ColumnValueKind.DateTime, TreeFilterOperator.In, dateOnly: true);
        list.ShouldBeAssignableTo<IEnumerable<object?>>().ShouldBe([new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 1)]);
    }

    [Fact]
    public void DateColumn_InvalidLiteral_FailsWithoutEchoingTheValue()
    {
        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            FilterValueCoercion.Coerce("not-a-date-xyz", ColumnValueKind.DateTime, TreeFilterOperator.Eq, dateOnly: true));
        ex.Message.ShouldNotContain("xyz");
    }

    [Fact]
    public void TimestampColumn_KeepsUtcInstant()
    {
        var value = FilterValueCoercion.Coerce("2026-10-07T12:00:00", ColumnValueKind.DateTime, TreeFilterOperator.Eq);
        value.ShouldBe(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("date", true)]
    [InlineData("DATE", true)]
    [InlineData(" date ", true)]
    [InlineData("timestamp", false)]
    [InlineData("datetime", false)]
    [InlineData("timestamp(3)", false)]
    [InlineData(null, false)]
    public void IsDateOnly_OnlyPlainDate(string? dataType, bool expected) =>
        ColumnValueKinds.IsDateOnly(dataType).ShouldBe(expected);
}
