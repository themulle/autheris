namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Integrations.Backstage;
using Autheris.Extensions.Backstage;
using Autheris.Application.Interfaces;
using Autheris.Application.SchemaRegistry;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class BackstageIntegrationTests
{
    [Fact]
    public async Task ExportCatalogEntitiesAsync_DefaultOptions_ContainsFederatedAndOpenApiEntities()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions
        {
            Backstage = new BackstageIntegrationOptions
            {
                DefaultOwner = "group:team-core",
                DefaultSystem = "system-gateway"
            }
        });

        var service = new BackstageCatalogExportService(options);

        // Act
        var entities = await service.ExportCatalogEntitiesAsync();

        // Assert
        entities.Count.ShouldBeGreaterThanOrEqualTo(2);

        var federated = entities.FirstOrDefault(e => e.Metadata.Name == "autheris-federated");
        federated.ShouldNotBeNull();
        federated.Kind.ShouldBe("API");
        federated.Spec.Type.ShouldBe("graphql");
        federated.Spec.Owner.ShouldBe("group:team-core");
        federated.Spec.System.ShouldBe("system-gateway");
        federated.Metadata.Annotations.ShouldContainKey("backstage.io/managed-by-location");

        var openApi = entities.FirstOrDefault(e => e.Metadata.Name == "autheris-openapi");
        openApi.ShouldNotBeNull();
        openApi.Kind.ShouldBe("API");
        openApi.Spec.Type.ShouldBe("openapi");
        openApi.Metadata.Links.ShouldContain(l => l.Title == "Swagger UI");
    }

    [Fact]
    public async Task ExportCatalogEntitiesAsync_WithSubgraphs_ExportsEachSubgraphAsApiEntity()
    {
        // Arrange
        var schemaRegistry = Substitute.For<ISchemaRegistryService>();
        schemaRegistry.GetAllServicesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Accounts", "Inventory" });

        schemaRegistry.GetLatestSchemaAsync("Accounts", Arg.Any<CancellationToken>())
            .Returns(new RegisteredSchema
            {
                ServiceName = "Accounts",
                Version = "1.2.0",
                Sdl = "type Query { account(id: ID!): Account }",
                GitCommit = "abc1234",
                GitBranch = "main",
                RegisteredBy = "alice",
                IsActive = true
            });

        schemaRegistry.GetLatestSchemaAsync("Inventory", Arg.Any<CancellationToken>())
            .Returns(new RegisteredSchema
            {
                ServiceName = "Inventory",
                Version = "0.9.1",
                Sdl = "type Query { inventoryCount: Int }",
                GitCommit = "def5678",
                GitBranch = "release/v0.9",
                RegisteredBy = "bob",
                IsActive = false // Deprecated
            });

        var options = Options.Create(new GatewayOptions());
        var service = new BackstageCatalogExportService(options, schemaRegistry: schemaRegistry);

        // Act
        var entities = await service.ExportCatalogEntitiesAsync();

        // Assert
        var accounts = entities.FirstOrDefault(e => e.Metadata.Name == "subgraph-accounts");
        accounts.ShouldNotBeNull();
        accounts.Spec.Type.ShouldBe("graphql");
        accounts.Spec.Lifecycle.ShouldBe("production");
        accounts.Spec.Owner.ShouldBe("user:alice");
        accounts.Spec.Definition.ShouldNotBeNull();
        accounts.Spec.Definition.ShouldContain("type Query { account");
        accounts.Metadata.Annotations["autheris.io/version"].ShouldBe("1.2.0");
        accounts.Metadata.Annotations["autheris.io/git-commit"].ShouldBe("abc1234");

        var inventory = entities.FirstOrDefault(e => e.Metadata.Name == "subgraph-inventory");
        inventory.ShouldNotBeNull();
        inventory.Spec.Lifecycle.ShouldBe("deprecated");
        inventory.Spec.Owner.ShouldBe("user:bob");
    }

    [Fact]
    public async Task ExportCatalogEntitiesAsync_WithGovernedTables_ExportsTablesWithEnrichedMetadata()
    {
        // Arrange
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TableMetadata>
            {
                new()
                {
                    Table = new()
                    {
                        SchemaName = "crm",
                        TableName = "customers",
                        DisplayName = "CRM Customers",
                        Description = "Global customer master data synced with dbt.",
                        DocumentationSource = "dbt",
                        Sensitivity = "HIGH",
                        RequiresFourEyes = true,
                        SourceType = "PostgreSQL"
                    }
                }
            });

        var options = Options.Create(new GatewayOptions
        {
            Backstage = new BackstageIntegrationOptions
            {
                IncludeTablesAsApis = true
            }
        });

        var service = new BackstageCatalogExportService(options, tableRepository: tableRepo);

        // Act
        var entities = await service.ExportCatalogEntitiesAsync();

        // Assert
        var tableEntity = entities.FirstOrDefault(e => e.Metadata.Name == "data-crm-customers");
        tableEntity.ShouldNotBeNull();
        tableEntity.Metadata.Title.ShouldBe("CRM Customers");
        tableEntity.Metadata.Description.ShouldBe("Global customer master data synced with dbt.");
        tableEntity.Metadata.Tags.ShouldContain("data-product");
        tableEntity.Metadata.Tags.ShouldContain("four-eyes-required");
        tableEntity.Metadata.Tags.ShouldContain("pii");
        tableEntity.Metadata.Tags.ShouldContain("dbt");
        tableEntity.Metadata.Annotations["autheris.io/source-type"].ShouldBe("PostgreSQL");
        tableEntity.Metadata.Annotations["autheris.io/requires-four-eyes"].ShouldBe("true");
        tableEntity.Metadata.Annotations["autheris.io/documentation-source"].ShouldBe("dbt");
    }

    [Fact]
    public async Task ExportCatalogEntitiesYamlAsync_ProducesValidMultiDocumentYaml()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions());
        var service = new BackstageCatalogExportService(options);

        // Act
        var yaml = await service.ExportCatalogEntitiesYamlAsync();

        // Assert
        yaml.ShouldNotBeNullOrWhiteSpace();
        yaml.ShouldContain("---");
        yaml.ShouldContain("apiVersion: backstage.io/v1alpha1");
        yaml.ShouldContain("kind: API");
        yaml.ShouldContain("name: autheris-federated");
        yaml.ShouldContain("name: autheris-openapi");
    }

    [Fact]
    public async Task ExportEntityByNameAsync_ExistingAndMissing_ReturnsExpected()
    {
        // Arrange
        var options = Options.Create(new GatewayOptions());
        var service = new BackstageCatalogExportService(options);

        // Act & Assert
        var found = await service.ExportEntityByNameAsync("autheris-federated");
        found.ShouldNotBeNull();
        found.Metadata.Name.ShouldBe("autheris-federated");

        var missing = await service.ExportEntityByNameAsync("non-existent-entity");
        missing.ShouldBeNull();
    }

    [Theory]
    [InlineData("My Service Name!", "my-service-name")]
    [InlineData("schema_v2.0--table", "schema_v2.0-table")]
    [InlineData("   spaces   ", "spaces")]
    [InlineData("", "unnamed")]
    public void SanitizeEntityName_NormalizesToBackstageCompliantName(string input, string expected)
    {
        var result = BackstageCatalogExportService.SanitizeEntityName(input);
        result.ShouldBe(expected);
    }
}
