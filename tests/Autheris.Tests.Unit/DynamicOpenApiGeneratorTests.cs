namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DynamicOpenApiGeneratorTests
{
    private static List<TableMetadata> CreateSampleTables()
    {
        return new List<TableMetadata>
        {
            new TableMetadata
            {
                Identifier = new TableIdentifier("finance", "dbo", "invoices"),
                Table = new Table { SchemaName = "dbo", TableName = "invoices", DisplayName = "Monthly Invoices" },
                PrimaryKeyColumns = ["id"],
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "customer_name", DataType = "varchar" },
                    new TableColumn { ColumnName = "amount", DataType = "decimal" },
                    new TableColumn { ColumnName = "iban", DataType = "varchar", IsSensitive = true },
                    new TableColumn { ColumnName = "invoice_date", DataType = "date" }
                ]
            },
            new TableMetadata
            {
                Identifier = new TableIdentifier("sales", "dbo", "leads"),
                Table = new Table { SchemaName = "dbo", TableName = "leads", DisplayName = "Sales Leads" },
                PrimaryKeyColumns = ["lead_id"],
                Columns =
                [
                    new TableColumn { ColumnName = "lead_id", DataType = "integer" },
                    new TableColumn { ColumnName = "company", DataType = "varchar" }
                ]
            }
        };
    }

    [Fact]
    public async Task GenerateOpenApiJsonAsync_WithAllTables_ProducesValidOpenApi31Spec()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await generator.GenerateOpenApiJsonAsync(null);

        json.ShouldNotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("openapi").GetString().ShouldBe("3.1.0");
        root.GetProperty("info").GetProperty("title").GetString()!.ShouldContain("Autheris");

        var paths = root.GetProperty("paths");
        paths.TryGetProperty("/finance/dbo/invoices", out var invoicesPath).ShouldBeTrue();
        paths.TryGetProperty("/sales/dbo/leads", out var leadsPath).ShouldBeTrue();

        var getInvoices = invoicesPath.GetProperty("get");
        getInvoices.GetProperty("summary").GetString().ShouldBe("Query finance.invoices");

        var parameters = getInvoices.GetProperty("parameters");
        parameters.GetArrayLength().ShouldBe(5); // $select, $filter, $top, $skip, $count

        var schemas = root.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("finance_dbo_invoices", out var invoiceSchema).ShouldBeTrue();

        var props = invoiceSchema.GetProperty("properties");
        props.GetProperty("id").GetProperty("type").GetString().ShouldBe("integer");
        props.GetProperty("amount").GetProperty("type").GetString().ShouldBe("number");
        props.GetProperty("iban").GetProperty("x-sensitive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GenerateOpenApiJsonAsync_WithDomainScope_FiltersPathsOnlyToTargetDomain()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await generator.GenerateOpenApiJsonAsync("finance");

        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        paths.TryGetProperty("/finance/dbo/invoices", out _).ShouldBeTrue();
        paths.TryGetProperty("/sales/dbo/leads", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GenerateOpenApiYamlAsync_ProducesValidYamlStructure()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var yaml = await generator.GenerateOpenApiYamlAsync(null);

        yaml.ShouldContain("openapi: \"3.1.0\"");
        yaml.ShouldContain("/finance/dbo/invoices:");
        yaml.ShouldContain("/sales/dbo/leads:");
        yaml.ShouldContain("components:");
    }

    [Fact]
    public async Task OpenApiCacheManager_CachesOutputAndInvalidatesCorrectly()
    {
        var cacheManager = new OpenApiCacheManager();
        int factoryInvocationCount = 0;

        Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryInvocationCount);
            return Task.FromResult("{\"openapi\": \"3.1.0\"}");
        }

        var bytes1 = await cacheManager.GetOrAddAsync("finance", isYaml: false, Factory);
        var bytes2 = await cacheManager.GetOrAddAsync("finance", isYaml: false, Factory);

        bytes1.ShouldBe(bytes2);
        factoryInvocationCount.ShouldBe(1);

        cacheManager.InvalidateCache();

        var bytes3 = await cacheManager.GetOrAddAsync("finance", isYaml: false, Factory);
        factoryInvocationCount.ShouldBe(2);
    }

    [Fact]
    public async Task GenerateOpenApiJsonAsync_WithModularTrue_EmploysExternalRefsForSchemas()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await generator.GenerateOpenApiJsonAsync(null, modular: true);

        using var doc = JsonDocument.Parse(json);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        schemas.TryGetProperty("finance_dbo_invoices", out var invoiceSchema).ShouldBeTrue();
        invoiceSchema.TryGetProperty("$ref", out var refProp).ShouldBeTrue();
        refProp.GetString().ShouldBe("/odata/v4/$openapi/schemas/finance/dbo/invoices");

        schemas.TryGetProperty("sales_dbo_leads", out var leadSchema).ShouldBeTrue();
        leadSchema.TryGetProperty("$ref", out var leadRefProp).ShouldBeTrue();
        leadRefProp.GetString().ShouldBe("/odata/v4/$openapi/schemas/sales/dbo/leads");
    }

    [Fact]
    public async Task GenerateEntitySchemaJsonAsync_ReturnsIsolatedTableSchema()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var schemaJson = await generator.GenerateEntitySchemaJsonAsync(new TableIdentifier("finance", "dbo", "invoices"));

        schemaJson.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(schemaJson!);
        var root = doc.RootElement;

        root.GetProperty("title").GetString().ShouldBe("finance_dbo_invoices");
        root.GetProperty("type").GetString().ShouldBe("object");
        root.GetProperty("properties").TryGetProperty("customer_name", out _).ShouldBeTrue();
        root.GetProperty("properties").GetProperty("iban").GetProperty("x-sensitive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetIndexDocumentAsync_ReturnsAggregatedDomainCatalog()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var index = await generator.GetIndexDocumentAsync("https://gateway.example.com");

        index.TotalTables.ShouldBe(2);
        index.Domains.Count.ShouldBe(2);

        var financeDomain = index.Domains.FirstOrDefault(d => d.Domain == "finance");
        financeDomain.ShouldNotBeNull();
        financeDomain!.TableCount.ShouldBe(1);
        financeDomain.JsonUrl.ShouldBe("https://gateway.example.com/odata/v4/finance/openapi.json");
        financeDomain.YamlUrl.ShouldBe("https://gateway.example.com/odata/v4/finance/openapi.yaml");

        index.Apis.Count.ShouldBeGreaterThan(0);
        index.Apis.ShouldContain(a => a.Name == "All Domains (Monolithic)");
    }

    [Fact]
    public async Task OpenApiCacheManager_DistinguishesModularAndStandardKeys()
    {
        var cacheManager = new OpenApiCacheManager();
        int factoryInvocationCount = 0;

        Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryInvocationCount);
            return Task.FromResult("{\"openapi\": \"3.1.0\"}");
        }

        var standard = await cacheManager.GetOrAddAsync("finance", false, false, Factory);
        var modular = await cacheManager.GetOrAddAsync("finance", false, true, Factory);

        standard.ShouldNotBeSameAs(modular);
        factoryInvocationCount.ShouldBe(2);
    }
}
