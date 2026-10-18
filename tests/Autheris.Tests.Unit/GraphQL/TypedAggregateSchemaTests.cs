using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A1-11 / SEC-112-27: the typed aggregate names join the collision set of the #157 schema model.</summary>
public sealed class TypedAggregateSchemaTests
{
    internal static TableMetadata Table(string domain, string schema, string name, params (string Name, string Type)[] columns) =>
        new()
        {
            Identifier = new TableIdentifier(domain, schema, name),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = (columns.Length == 0 ? [("id", "int")] : columns)
                .Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
            PrimaryKeyColumns = ["id"]
        };

    internal static async Task<CatalogSchemaModel> BuildAsync(params TableMetadata[] tables)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tables.ToList());
        var relRepo = Substitute.For<ITableRelationRepository>();
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
        return await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);
    }

    [Fact]
    public async Task Aggregate_TableNamedXAggregate_OmittedWithNameCollision()
    {
        var x = Table("sales", "dbo", "x");
        var xAggregate = Table("sales", "dbo", "x_aggregate");

        var model = await BuildAsync(x, xAggregate);

        model.Tables.Select(t => t.Identifier).ShouldBe([x.Identifier]);
        var omitted = model.OmittedTables.ShouldHaveSingleItem();
        omitted.Table.ShouldBe(xAggregate.Identifier);
        omitted.Reason.ShouldBe(CatalogTableOmissionReason.NameCollision);
    }

    [Theory]
    [InlineData("column")]
    [InlineData("aggregate_input")]
    [InlineData("aggregate_order_by")]
    [InlineData("aggregate_key")]
    [InlineData("aggregate_group")]
    [InlineData("aggregate_result")]
    public async Task Aggregate_TableNamedLikeAGeneratedAggregateType_IsOmittedWithNameCollision(string suffix)
    {
        var x = Table("sales", "dbo", "x");
        var clash = Table("sales", "dbo", "x_" + suffix);

        var model = await BuildAsync(x, clash);

        model.Tables.Select(t => t.Identifier).ShouldBe([x.Identifier]);
        model.OmittedTables.ShouldHaveSingleItem().Reason.ShouldBe(CatalogTableOmissionReason.NameCollision);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Aggregate_TableNamedCollision_HiddenTableDoesNotAffectVisibleSchema()
    {
        var x = Table("sales", "dbo", "x");
        var clash = Table("sales", "dbo", "x_aggregate_result");
        var other = Table("sales", "dbo", "y");

        var a = await BuildAsync(x, clash, other);
        var b = await BuildAsync(other, clash, x);

        a.Tables.Select(t => t.TypeName).ShouldBe(b.Tables.Select(t => t.TypeName));
        a.OmittedTables.Select(o => o.Table).ShouldBe(b.OmittedTables.Select(o => o.Table));
        a.Fingerprint().ShouldBe(b.Fingerprint());
    }

    [Fact]
    public void AggregateTypeNames_AreDerivedFromTheTableTypeName()
    {
        var names = AggregateTypeNames.For("sales_dbo_orders");

        names.Column.ShouldBe("sales_dbo_orders_column");
        names.Input.ShouldBe("sales_dbo_orders_aggregate_input");
        names.OrderBy.ShouldBe("sales_dbo_orders_aggregate_order_by");
        names.Key.ShouldBe("sales_dbo_orders_aggregate_key");
        names.Group.ShouldBe("sales_dbo_orders_aggregate_group");
        names.Result.ShouldBe("sales_dbo_orders_aggregate_result");
        names.Field.ShouldBe("sales_dbo_orders_aggregate");
    }

    [Fact]
    public void AggregateTypeNames_SharedNames_AreReservedForTheSchemaModel()
    {
        AggregateTypeNames.SharedTypeNames.ShouldContain("AutherisAggregateFn");
        AggregateTypeNames.SharedTypeNames.ShouldContain("AutherisValueKind");
        AggregateTypeNames.SharedTypeNames.ShouldContain("AutherisAggregateValue");
        AggregateTypeNames.SharedTypeNames.ShouldContain("AutherisTypedFilter");
        AggregateTypeNames.SharedTypeNames.ShouldContain("AutherisMetricQueryResult");
    }
}
