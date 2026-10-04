namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.OData.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.OData;
using Autheris.GraphQL.DynamicTypes;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class OmnichannelDocumentationTests
{
    private static TableMetadata CreateDocumentedTable()
    {
        var metaDict = new Dictionary<string, string>
        {
            ["dbt_model"] = "models/marts/finance/mrr.sql",
            ["openmetadata_urn"] = "urn:table:corp.finance.mrr"
        };

        return new TableMetadata
        {
            Identifier = new TableIdentifier("finance", "dbo", "mrr"),
            Table = new Table
            {
                SchemaName = "dbo",
                TableName = "mrr",
                DisplayName = "Monthly Recurring Revenue",
                Description = "Monthly recurring revenue recognized according to IFRS 15.",
                LongDescription = "Calculated from active subscriptions at month closing. Excludes one-off setup fees."
            },
            PrimaryKeyColumns = ["account_id", "month"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "account_id",
                    DataType = "integer",
                    Description = "Unique identifier of the customer account.",
                    LongDescription = "Foreign key referencing accounts.id in core billing system.",
                    Meta = metaDict
                },
                new TableColumn
                {
                    ColumnName = "revenue_amount",
                    DataType = "decimal",
                    Description = "Net MRR in EUR.",
                    LongDescription = "Formula: sum(subscription_price) - discounts + expansions.",
                    Meta = metaDict
                },
                new TableColumn
                {
                    ColumnName = "iban",
                    DataType = "varchar",
                    IsSensitive = true,
                    Description = "Customer billing IBAN."
                }
            ]
        };
    }

    [Fact]
    public void Channel1_GraphQL_DynamicTableType_SynthesizesCommonMarkMarkdownDescriptions()
    {
        var table = CreateDocumentedTable();
        var masking = Substitute.For<IColumnMaskingProvider>();

        var tableType = new DynamicTableType(table, masking);

        var schema = SchemaBuilder.New()
            .AddType(tableType)
            .AddQueryType(d => d.Name("Query").Field("dummy").Resolve(_ => "ok"))
            .Create();

        var dynamicType = schema.Types.GetType<IObjectTypeDefinition>("finance_mrr");
        dynamicType.ShouldNotBeNull();
        dynamicType.Description.ShouldBe("Monthly recurring revenue recognized according to IFRS 15.");

        var accountIdField = dynamicType.Fields["account_id"];
        accountIdField.Description.ShouldNotBeNull();
        accountIdField.Description!.ShouldContain("Unique identifier of the customer account.");
        accountIdField.Description!.ShouldContain("---");
        accountIdField.Description!.ShouldContain("**Ausführliche Spezifikation:**");
        accountIdField.Description!.ShouldContain("Foreign key referencing accounts.id");

        var revenueField = dynamicType.Fields["revenue_amount"];
        revenueField.Description.ShouldNotBeNull();
        revenueField.Description!.ShouldContain("Net MRR in EUR.");
        revenueField.Description!.ShouldContain("Formula: sum(subscription_price)");

        var ibanField = dynamicType.Fields["iban"];
        ibanField.Description.ShouldBe("Customer billing IBAN.");
    }

    [Fact]
    public async Task Channel2_MCP_SemanticMcpCompiler_ExposesToolDescriptionsAndColumnDocsResources()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var table = CreateDocumentedTable();
        repo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var compiler = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance);

        // Tool compilation
        var toolDef = await compiler.CompileToolAsync("query_mrr", table.Identifier);
        toolDef.Description.ShouldContain("Monthly recurring revenue");
        toolDef.InputJsonSchema.ShouldContain("\"columns\"");
        toolDef.InputJsonSchema.ShouldContain("Net MRR in EUR.");

        // Semantic resources
        var resources = await compiler.GetSemanticResourcesAsync("finance");
        resources.Count.ShouldBe(5); // 1 glossary + 1 lineage + 3 columns

        var colResource = resources.FirstOrDefault(r => r.Uri == "dbt://models/mrr/columns/revenue_amount/docs");
        colResource.ShouldNotBeNull();
        colResource.Text.ShouldContain("# Column Documentation: mrr.revenue_amount");
        colResource.Text.ShouldContain("Net MRR in EUR.");
        colResource.Text.ShouldContain("## Detailed Specification");
        colResource.Text.ShouldContain("Formula: sum(subscription_price)");
        colResource.Text.ShouldContain("models/marts/finance/mrr.sql");
    }

    [Fact]
    public async Task Channel3_OpenAPI_DynamicOpenApiGenerator_PopulatesDescriptionAndVendorExtensions()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var table = CreateDocumentedTable();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await generator.GenerateOpenApiJsonAsync(null);

        using var doc = JsonDocument.Parse(json);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var mrrSchema = schemas.GetProperty("finance_dbo_mrr");

        mrrSchema.GetProperty("description").GetString().ShouldBe("Monthly recurring revenue recognized according to IFRS 15.");
        mrrSchema.GetProperty("x-long-description").GetString()!.ShouldContain("Calculated from active subscriptions");

        var props = mrrSchema.GetProperty("properties");
        var revProp = props.GetProperty("revenue_amount");
        revProp.GetProperty("description").GetString().ShouldBe("Net MRR in EUR.");
        revProp.GetProperty("x-long-description").GetString()!.ShouldContain("Formula: sum(subscription_price)");
        revProp.GetProperty("x-dbt-meta").GetProperty("dbt_model").GetString().ShouldBe("models/marts/finance/mrr.sql");

        var ibanProp = props.GetProperty("iban");
        ibanProp.GetProperty("description").GetString().ShouldBe("Customer billing IBAN.");
        ibanProp.GetProperty("x-sensitive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Channel4_OData_ODataCsdlGenerator_EmitsOasisCsdlAnnotationTerms()
    {
        var table = CreateDocumentedTable();
        var xml = ODataCsdlGenerator.GenerateMetadataXml([table]);

        xml.ShouldContain("<Annotation Term=\"Core.Description\" String=\"Monthly recurring revenue recognized according to IFRS 15.\" />");
        xml.ShouldContain("<Annotation Term=\"Core.LongDescription\" String=\"Calculated from active subscriptions at month closing. Excludes one-off setup fees.\" />");

        xml.ShouldContain("<Property Name=\"revenue_amount\" Type=\"Edm.Decimal\">");
        xml.ShouldContain("<Annotation Term=\"Core.Description\" String=\"Net MRR in EUR.\" />");
        xml.ShouldContain("<Annotation Term=\"Core.LongDescription\" String=\"Formula: sum(subscription_price) - discounts + expansions.\" />");

        xml.ShouldContain("<Property Name=\"iban\" Type=\"Edm.String\">");
        xml.ShouldContain("<Annotation Term=\"Core.Description\" String=\"Customer billing IBAN.\" />");
    }
}
