namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DocumentationSourcePriorityTests
{
    private static SqliteGovernanceRepository CreateRepository(List<string>? precedence = null)
    {
        var epochService = new EpochValidationService();
        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                DocumentationSourcePrecedence = precedence ?? new List<string>
                {
                    "Manual",
                    "DataCatalog",
                    "dbt",
                    "OpenApi",
                    "Database",
                    "Default"
                }
            }
        });

        return new SqliteGovernanceRepository(epochService, options);
    }

    [Fact]
    public async Task HigherPriority_DataCatalog_CannotBeOverwritten_ByLowerPriority_OpenApi()
    {
        using var repository = CreateRepository();
        var tableId = new TableIdentifier("sales", "dbo", "orders");

        // 1. Initial insert from DataCatalog (Rank 1)
        var initial = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                DisplayName = "Orders Table",
                Description = "Catalog: Verified enterprise orders",
                LongDescription = "Catalog: Deep architectural documentation for orders",
                DocumentationSource = "DataCatalog"
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    Description = "Catalog: Primary Key",
                    DocumentationSource = "DataCatalog"
                },
                new TableColumn
                {
                    ColumnName = "amount",
                    DataType = "numeric",
                    Description = "Catalog: Order total in EUR",
                    DocumentationSource = "DataCatalog"
                }
            ]
        };

        await repository.UpsertTableMetadataAsync(initial);

        // 2. Subsequent sync from OpenApi (Rank 3)
        var openApiUpdate = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                DisplayName = "Orders Table",
                Description = "OpenApi: auto-generated orders doc",
                LongDescription = "OpenApi: swagger generated long doc",
                DocumentationSource = "OpenApi"
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    Description = "OpenApi: id param",
                    DocumentationSource = "OpenApi"
                },
                new TableColumn
                {
                    ColumnName = "amount",
                    DataType = "numeric",
                    Description = "OpenApi: amount field",
                    DocumentationSource = "OpenApi"
                }
            ]
        };

        await repository.UpsertTableMetadataAsync(openApiUpdate);

        // 3. Verify DataCatalog descriptions remained intact
        var result = await repository.GetTableMetadataAsync(tableId);
        result.ShouldNotBeNull();
        result.Table.Description.ShouldBe("Catalog: Verified enterprise orders");
        result.Table.LongDescription.ShouldBe("Catalog: Deep architectural documentation for orders");
        result.Table.DocumentationSource.ShouldBe("DataCatalog");

        var amountCol = result.Columns.FirstOrDefault(c => c.ColumnName == "amount");
        amountCol.ShouldNotBeNull();
        amountCol.Description.ShouldBe("Catalog: Order total in EUR");
        amountCol.DocumentationSource.ShouldBe("DataCatalog");
    }

    [Fact]
    public async Task HighestPriority_Manual_Overwrites_LowerPriority_OpenApi()
    {
        using var repository = CreateRepository();
        var tableId = new TableIdentifier("crm", "dbo", "customers");

        // 1. Initial insert from OpenApi
        var openApiMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "crm",
                SchemaName = "dbo",
                TableName = "customers",
                DisplayName = "Customers",
                Description = "OpenApi: Customer endpoint entity",
                DocumentationSource = "OpenApi"
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    Description = "OpenApi: Identifier",
                    DocumentationSource = "OpenApi"
                }
            ]
        };

        await repository.UpsertTableMetadataAsync(openApiMeta);

        // 2. Manual overwrite (Rank 0 - Highest)
        var manualMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "crm",
                SchemaName = "dbo",
                TableName = "customers",
                DisplayName = "Customers",
                Description = "Curated manually by Data Governance Officer",
                DocumentationSource = "Manual"
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    Description = "Primary customer identity key",
                    DocumentationSource = "Manual"
                }
            ]
        };

        await repository.UpsertTableMetadataAsync(manualMeta);

        // 3. Verify Manual won
        var result = await repository.GetTableMetadataAsync(tableId);
        result.ShouldNotBeNull();
        result.Table.Description.ShouldBe("Curated manually by Data Governance Officer");
        result.Table.DocumentationSource.ShouldBe("Manual");

        var idCol = result.Columns.FirstOrDefault(c => c.ColumnName == "id");
        idCol.ShouldNotBeNull();
        idCol.Description.ShouldBe("Primary customer identity key");
        idCol.DocumentationSource.ShouldBe("Manual");
    }

    [Fact]
    public async Task LowerPrioritySource_Fills_MissingOrEmptyDocumentation()
    {
        using var repository = CreateRepository();
        var tableId = new TableIdentifier("billing", "dbo", "invoices");

        // 1. Table created by Database reflection without descriptions
        var dbMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "billing",
                SchemaName = "dbo",
                TableName = "invoices",
                DisplayName = "Invoices",
                Description = null,
                LongDescription = null,
                DocumentationSource = "Database"
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    Description = null,
                    DocumentationSource = "Database"
                },
                new TableColumn
                {
                    ColumnName = "notes",
                    DataType = "text",
                    Description = null,
                    DocumentationSource = "Database"
                }
            ]
        };

        await repository.UpsertTableMetadataAsync(dbMeta);

        // 2. Ingestion from OpenApi (Rank 3) fills in the empty descriptions
        var openApiMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "billing",
                SchemaName = "dbo",
                TableName = "invoices",
                DisplayName = "Invoices",
                Description = "Invoices issued to clients",
                DocumentationSource = "OpenApi"
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    Description = "Invoice ID",
                    DocumentationSource = "OpenApi"
                },
                new TableColumn
                {
                    ColumnName = "notes",
                    DataType = "text",
                    Description = "Special invoice notes",
                    DocumentationSource = "OpenApi"
                }
            ]
        };

        await repository.UpsertTableMetadataAsync(openApiMeta);

        // 3. Verify descriptions were filled
        var result = await repository.GetTableMetadataAsync(tableId);
        result.ShouldNotBeNull();
        result.Table.Description.ShouldBe("Invoices issued to clients");
        result.Table.DocumentationSource.ShouldBe("OpenApi");

        var notesCol = result.Columns.FirstOrDefault(c => c.ColumnName == "notes");
        notesCol.ShouldNotBeNull();
        notesCol.Description.ShouldBe("Special invoice notes");
        notesCol.DocumentationSource.ShouldBe("OpenApi");
    }

    [Fact]
    public async Task CustomPrecedenceOrder_Configured_IsRespected()
    {
        // Custom precedence: OpenApi beats DataCatalog
        var customPrecedence = new List<string> { "OpenApi", "DataCatalog" };
        using var repository = CreateRepository(customPrecedence);
        var tableId = new TableIdentifier("analytics", "dbo", "clicks");

        var catalogMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "analytics",
                SchemaName = "dbo",
                TableName = "clicks",
                DisplayName = "Clicks",
                Description = "Catalog description",
                DocumentationSource = "DataCatalog"
            },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer", Description = "Catalog col", DocumentationSource = "DataCatalog" }]
        };

        await repository.UpsertTableMetadataAsync(catalogMeta);

        var openApiMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceType = "sql",
                SourceName = "analytics",
                SchemaName = "dbo",
                TableName = "clicks",
                DisplayName = "Clicks",
                Description = "OpenApi description",
                DocumentationSource = "OpenApi"
            },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer", Description = "OpenApi col", DocumentationSource = "OpenApi" }]
        };

        await repository.UpsertTableMetadataAsync(openApiMeta);

        var result = await repository.GetTableMetadataAsync(tableId);
        result.ShouldNotBeNull();
        result.Table.Description.ShouldBe("OpenApi description");
        result.Table.DocumentationSource.ShouldBe("OpenApi");
    }
}
