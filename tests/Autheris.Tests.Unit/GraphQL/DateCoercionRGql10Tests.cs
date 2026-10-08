namespace Autheris.Tests.Unit.GraphQL;

using System;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;

/// <summary>
/// R-GQL-10: filter dates without offset are UTC (not server local time), invalid values are rejected, and SQLite
/// receives ISO-8601 ("T", "Z") instead of the provider's space separated format.
/// </summary>
public sealed class DateCoercionRGql10Tests
{
    [Fact]
    public void UnspecifiedDateTime_IsTreatedAsUtc()
    {
        var value = GraphQlTreeBuilder.CoerceValue(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Unspecified), CatalogFieldType.DateTime, TreeFilterOperator.Eq);

        value.ShouldBe(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void StringWithoutOffset_IsTreatedAsUtc()
    {
        var value = GraphQlTreeBuilder.CoerceValue("2026-10-07T12:00:00", CatalogFieldType.DateTime, TreeFilterOperator.Eq);

        value.ShouldBe(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void InvalidDate_IsRejected()
    {
        Should.Throw<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(() =>
            GraphQlTreeBuilder.CoerceValue("not-a-date", CatalogFieldType.DateTime, TreeFilterOperator.Eq));
    }

    [Theory]
    [InlineData(DatabaseDialect.Sqlite, "2026-10-07T10:00:00Z")]
    [InlineData(DatabaseDialect.PostgreSql, null)]
    public void SqliteDates_AreBoundAsIso8601Utc(DatabaseDialect dialect, string? expected)
    {
        var dto = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(2));

        var bound = GovernedTreeQueryService.ToProviderValue(dto, dialect);

        if (expected != null)
        {
            bound.ShouldBe(expected);
        }
        else
        {
            bound.ShouldBe(dto);
        }
    }
}
