namespace Autheris.Tests.Unit.Catalog.Search;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class CatalogSearchDocumentBuilderForeignKeyTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static TableMetadata Table(IReadOnlyList<TableColumn> columns, params string[] primaryKey) => new()
    {
        Identifier = Orders,
        Table = new Table { SchemaName = "dbo", TableName = "orders", IsActive = true },
        Columns = columns,
        PrimaryKeyColumns = primaryKey
    };

    [Fact]
    public void BuildForeignKeys_ColumnMeta_IsForeignKeyType()
    {
        var table = Table([new TableColumn
        {
            ColumnName = "buyer",
            DataType = "int",
            Meta = new Dictionary<string, string> { ["foreign_key_table"] = "people", ["foreign_key_column"] = "pid" }
        }]);

        var fk = CatalogSearchDocumentBuilder.BuildForeignKeys(table).ShouldNotBeNull().ShouldHaveSingleItem();

        fk.Type.ShouldBe(TableRelationshipType.ForeignKey);
        fk.ToTable.TableName.ShouldBe("people");
        fk.ToColumn.ShouldBe("pid");
    }

    [Fact]
    public void BuildForeignKeys_NamingRule_IsSemanticInferredType()
    {
        var table = Table([new TableColumn { ColumnName = "customer_id", DataType = "int" }]);

        var fk = CatalogSearchDocumentBuilder.BuildForeignKeys(table).ShouldNotBeNull().ShouldHaveSingleItem();

        fk.Type.ShouldBe(TableRelationshipType.SemanticInferred);
        fk.ToTable.TableName.ShouldBe("customers");
        fk.Incoming.ShouldBeFalse();
    }

    [Fact]
    public void BuildForeignKeys_PrimaryKeyOrHiddenColumn_Skipped()
    {
        var table = Table(
            [
                new TableColumn { ColumnName = "order_id", DataType = "int" },
                new TableColumn { ColumnName = "region_id", DataType = "int", DiscoveryHidden = true }
            ],
            "order_id");

        CatalogSearchDocumentBuilder.BuildForeignKeys(table).ShouldBeNull();
    }
}
