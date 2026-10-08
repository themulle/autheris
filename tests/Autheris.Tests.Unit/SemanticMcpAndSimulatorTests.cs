namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SemanticMcpAndSimulatorTests
{
    private static TableMetadata CreateSampleTable()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("finance", "dbo", "monthly_revenue"),
            Table = new Table { SchemaName = "dbo", TableName = "monthly_revenue", DisplayName = "Monthly Revenue Aggregates" },
            PrimaryKeyColumns = ["account_id", "month"],
            Columns =
            [
                new TableColumn { ColumnName = "account_id", DataType = "integer" },
                new TableColumn { ColumnName = "month", DataType = "date" },
                new TableColumn { ColumnName = "gross_revenue", DataType = "decimal" },
                new TableColumn { ColumnName = "net_revenue", DataType = "decimal" },
                new TableColumn { ColumnName = "iban", DataType = "varchar", IsSensitive = true }
            ]
        };
    }

    [Fact]
    public async Task SemanticMcpCompiler_CompileToolAsync_EnforcesCompactDescriptionAndSchema()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var table = CreateSampleTable();
        repo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var compiler = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance);
        var toolDef = await compiler.CompileToolAsync("query_monthly_revenue", table.Identifier);

        toolDef.Name.ShouldBe("query_monthly_revenue");
        toolDef.Description.Length.ShouldBeLessThanOrEqualTo(120);
        toolDef.Description.ShouldContain("monthly_revenue");
        toolDef.InputJsonSchema.ShouldContain("\"first\"");
        toolDef.TargetGraphQLOperation.ShouldContain("table(domain: \"finance\"");
    }

    [Fact]
    public async Task SemanticMcpCompiler_GetSemanticResourcesAsync_YieldsGlossary_AndNoInventedLineage()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var table = CreateSampleTable();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { table }));

        var compiler = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance);
        var resources = await compiler.GetSemanticResourcesAsync("finance");

        resources.Count.ShouldBe(1);

        var glossary = resources.FirstOrDefault(r => r.Uri.StartsWith("glossary://", StringComparison.Ordinal));
        glossary.ShouldNotBeNull();
        glossary.Text.ShouldContain("gross_revenue");
        glossary.Text.ShouldContain("[SENSITIVE/MASKED]");

        // The catalog does not know the dbt model of a table; a lineage resource derived from the table name would be invented.
        resources.ShouldNotContain(r => r.Uri.EndsWith("/lineage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreFlightQuerySimulator_StandardQuery_IsAllowedWithAccurateMetrics()
    {
        var simulator = new PreFlightQuerySimulator(NullLogger<PreFlightQuerySimulator>.Instance);
        var query = "query { table(domain: \"finance\", name: \"monthly_revenue\", first: 20) { id revenue date } }";

        var result = await simulator.SimulateQueryAsync(query);

        result.IsAllowed.ShouldBeTrue();
        result.EstimatedRowCount.ShouldBe(20);
        result.EstimatedResponseTokens.ShouldBeGreaterThan(0);
        result.BlockReason.ShouldBeNull();
    }

    [Fact]
    public async Task PreFlightQuerySimulator_ExcessiveRowLimit_ExceedsSafetyLimitAndBlocks()
    {
        var simulator = new PreFlightQuerySimulator(NullLogger<PreFlightQuerySimulator>.Instance);
        // Requesting 1000 rows across multiple selections exceeds 4,000 tokens limit
        var query = "query { table(domain: \"finance\", name: \"large_table\", first: 500) { f1 f2 f3 f4 f5 f6 } }";

        var result = await simulator.SimulateQueryAsync(query);

        result.IsAllowed.ShouldBeFalse();
        result.BlockReason.ShouldNotBeNull();
        result.BlockReason.ShouldContain("Hard-Safety-Limit");
    }

    [Fact]
    public async Task PreFlightQuerySimulator_MalformedQuery_ReturnsSyntaxError()
    {
        var simulator = new PreFlightQuerySimulator(NullLogger<PreFlightQuerySimulator>.Instance);
        var malformedQuery = "query { invalid GraphQL {{ ";

        var result = await simulator.SimulateQueryAsync(malformedQuery);

        result.IsAllowed.ShouldBeFalse();
        result.BlockReason!.ShouldContain("Syntax");
    }

    [Fact]
    public async Task McpProvenanceEnricher_EnrichesPayloadWithLineageFootnote()
    {
        var enricher = new McpProvenanceEnricher(NullLogger<McpProvenanceEnricher>.Instance);
        var tableId = new TableIdentifier("finance", "dbo", "invoices");

        var provenance = await enricher.CreateProvenanceAsync(tableId);
        provenance.DbtModel.ShouldBe("models/marts/finance/invoices.sql");
        provenance.OpenMetadataUrn.ShouldContain("finance.invoices");

        var rawJson = "{\"invoices\":[{\"id\":101,\"amount\":500.0}]}";
        var enrichedJson = enricher.EnrichPayloadWithProvenance(rawJson, provenance);

        enrichedJson.ShouldContain("\"_provenance\":");
        enrichedJson.ShouldContain("models/marts/finance/invoices.sql");
    }
}
