namespace Autheris.Tests.Unit.GraphQL;

using System.Collections.Generic;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class Wave4ColumnSemanticsAndTemporalTests
{
    [Fact]
    public void TableColumn_WithDbtMeta_ExposesTypedColumnSemantics()
    {
        var col = new TableColumn
        {
            ColumnName = "gross_amount",
            DataType = "numeric",
            Description = "Total amount billed",
            Meta = new Dictionary<string, string>
            {
                ["role"] = "measure",
                ["unit"] = "EUR",
                ["business_name"] = "Gross Invoiced Amount"
            }
        };

        col.Semantics.ShouldNotBeNull();
        col.Semantics.Role.ShouldBe("measure");
        col.Semantics.Unit.ShouldBe("EUR");
        col.Semantics.BusinessName.ShouldBe("Gross Invoiced Amount");
        col.Semantics.RawMeta.ShouldNotBeNull();
    }

    [Fact]
    public void TableColumn_WithoutMeta_HasNullSemanticsProperties()
    {
        var col = new TableColumn
        {
            ColumnName = "id",
            DataType = "uuid"
        };

        col.Semantics.Role.ShouldBeNull();
        col.Semantics.Unit.ShouldBeNull();
        col.Semantics.BusinessName.ShouldBeNull();
    }

    [Fact]
    public void McpDatasetColumn_AcceptsColumnSemantics()
    {
        var semantics = new ColumnSemantics(Role: "dimension", Unit: null, BusinessName: "Customer Territory");
        var mcpCol = new McpDatasetColumn(
            Name: "territory",
            Type: "varchar",
            Description: "Sales territory",
            Sensitive: false,
            PrimaryKey: false,
            GraphQlField: "territory",
            GraphQlType: "String",
            Semantics: semantics
        );

        mcpCol.Semantics.ShouldNotBeNull();
        mcpCol.Semantics.Role.ShouldBe("dimension");
        mcpCol.Semantics.BusinessName.ShouldBe("Customer Territory");
    }
}
