using Autheris.Application.Analytics;
using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

/// <summary>#112 WP-1: the column value kind is the Application-level twin of the GraphQL field type.</summary>
public sealed class ColumnValueKindTests
{
    [Theory]
    [InlineData(null, ColumnValueKind.String)]
    [InlineData("", ColumnValueKind.String)]
    [InlineData("varchar(20)", ColumnValueKind.String)]
    [InlineData("bit", ColumnValueKind.Boolean)]
    [InlineData("boolean", ColumnValueKind.Boolean)]
    [InlineData("bigint", ColumnValueKind.Long)]
    [InlineData("int", ColumnValueKind.Int)]
    [InlineData("smallint", ColumnValueKind.Int)]
    [InlineData("decimal(10,2)", ColumnValueKind.Decimal)]
    [InlineData("numeric", ColumnValueKind.Decimal)]
    [InlineData("double", ColumnValueKind.Float)]
    [InlineData("datetime2", ColumnValueKind.DateTime)]
    [InlineData("timestamp", ColumnValueKind.DateTime)]
    [InlineData("point", ColumnValueKind.String)]
    [InlineData("interval", ColumnValueKind.String)]
    [InlineData("uuid", ColumnValueKind.String)]
    public void FromDataType_MapsTypes(string? dataType, ColumnValueKind expected) =>
        ColumnValueKinds.FromDataType(dataType).ShouldBe(expected);

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("money")]
    [InlineData("real")]
    [InlineData("bool")]
    [InlineData("date")]
    [InlineData("geometry")]
    [InlineData("nvarchar(max)")]
    public void CatalogSchemaModel_MapDataType_AgreesWithFromDataType(string dataType) =>
        CatalogSchemaModel.MapDataType(dataType).ToString().ShouldBe(ColumnValueKinds.FromDataType(dataType).ToString());
}
