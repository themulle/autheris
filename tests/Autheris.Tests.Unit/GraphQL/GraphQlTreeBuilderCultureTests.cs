using System.Globalization;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.GraphQL.Catalog;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class GraphQlTreeBuilderCultureTests
{
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture;
        private readonly CultureInfo _originalUiCulture;

        public CultureScope(string cultureName)
        {
            _originalCulture = CultureInfo.CurrentCulture;
            _originalUiCulture = CultureInfo.CurrentUICulture;

            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }

    [Fact]
    public void CoerceSingleValue_UnderGermanCulture_PreservesDecimalValueWithoutMultiplyingByTen()
    {
        using var _ = new CultureScope("de-DE");

        // In German culture, 1.5m.ToString() is "1,5".
        // If parsed with NumberStyles.Number and InvariantCulture, "," is a thousands separator, resulting in 15m!
        var coerced = GraphQlTreeBuilder.CoerceSingleValue(1.5m, CatalogFieldType.Decimal);

        Assert.NotNull(coerced);
        Assert.IsType<decimal>(coerced);
        Assert.Equal(1.5m, (decimal)coerced);
    }

    [Fact]
    public void CoerceSingleValue_UnderGermanCulture_PreservesDoubleValueWithoutThrowing()
    {
        using var _ = new CultureScope("de-DE");

        // In German culture, 1.5d.ToString() is "1,5".
        // double.TryParse with NumberStyles.Float rejects "1,5", causing GatewayInvalidQueryException.
        var coerced = GraphQlTreeBuilder.CoerceSingleValue(1.5d, CatalogFieldType.Float);

        Assert.NotNull(coerced);
        Assert.IsType<double>(coerced);
        Assert.Equal(1.5d, (double)coerced);
    }

    [Fact]
    public void CoerceSingleValue_RejectsCommaAsDecimalSeparatorInStringLiteral()
    {
        using var _ = new CultureScope("de-DE");

        // String "1,5" must NOT be parsed as 15m (which happens when AllowThousands is enabled).
        // It must be rejected because GraphQL / SQL literals require "." as decimal separator.
        Assert.Throws<GatewayInvalidQueryException>(() =>
            GraphQlTreeBuilder.CoerceSingleValue("1,5", CatalogFieldType.Decimal));
    }

    [Fact]
    public void CoerceSingleValue_ParsesInvariantDotDecimalStringCorrectly()
    {
        using var _ = new CultureScope("de-DE");

        var coerced = GraphQlTreeBuilder.CoerceSingleValue("1.5", CatalogFieldType.Decimal);

        Assert.NotNull(coerced);
        Assert.IsType<decimal>(coerced);
        Assert.Equal(1.5m, (decimal)coerced);
    }

    [Fact]
    public void CoerceSingleValue_ParsesInvariantDotFloatStringCorrectly()
    {
        using var _ = new CultureScope("de-DE");

        var coerced = GraphQlTreeBuilder.CoerceSingleValue("1.5", CatalogFieldType.Float);

        Assert.NotNull(coerced);
        Assert.IsType<double>(coerced);
        Assert.Equal(1.5d, (double)coerced);
    }

    [Fact]
    public void CoerceSingleValue_DateTimeOffsetPreservedUnderGermanCulture()
    {
        using var _ = new CultureScope("de-DE");

        var dto = new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);
        var coerced = GraphQlTreeBuilder.CoerceSingleValue(dto, CatalogFieldType.DateTime);

        Assert.NotNull(coerced);
        Assert.IsType<DateTimeOffset>(coerced);
        Assert.Equal(dto, (DateTimeOffset)coerced);
    }

    [Fact]
    public void CoerceSingleValue_IsoDateTimeStringParsedUnderGermanCulture()
    {
        using var _ = new CultureScope("de-DE");

        var coerced = GraphQlTreeBuilder.CoerceSingleValue("2026-10-07T14:00:00Z", CatalogFieldType.DateTime);

        Assert.NotNull(coerced);
        Assert.IsType<DateTimeOffset>(coerced);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero), (DateTimeOffset)coerced);
    }

    [Fact]
    public void CoerceSingleValue_FastPathPreservesIntegerTypes()
    {
        using var _ = new CultureScope("de-DE");

        Assert.Equal(42, GraphQlTreeBuilder.CoerceSingleValue(42, CatalogFieldType.Int));
        Assert.Equal(42, GraphQlTreeBuilder.CoerceSingleValue(42L, CatalogFieldType.Int));
        Assert.Equal(42L, GraphQlTreeBuilder.CoerceSingleValue(42L, CatalogFieldType.Long));
        Assert.Equal(42L, GraphQlTreeBuilder.CoerceSingleValue(42, CatalogFieldType.Long));
        Assert.True((bool)GraphQlTreeBuilder.CoerceSingleValue(true, CatalogFieldType.Boolean)!);
        Assert.False((bool)GraphQlTreeBuilder.CoerceSingleValue(false, CatalogFieldType.Boolean)!);
    }

    [Fact]
    public void CoerceValue_InOperatorWithDecimalsUnderGermanCulture()
    {
        using var _ = new CultureScope("de-DE");

        var rawList = new List<object?> { 1.5m, 2.75m };
        var coerced = GraphQlTreeBuilder.CoerceValue(rawList, CatalogFieldType.Decimal, TreeFilterOperator.In);

        Assert.NotNull(coerced);
        var list = Assert.IsAssignableFrom<List<object?>>(coerced);
        Assert.Equal(2, list.Count);
        Assert.Equal(1.5m, list[0]);
        Assert.Equal(2.75m, list[1]);
    }
}
