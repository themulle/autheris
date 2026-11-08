using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Localization;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class MultilingualCatalogGovernanceTests
{
    /// <summary>
    /// SEC-CAT146-06: Text-only updates bump content_version, not structure_version.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_CAT146_06_TextOnly_Updates_Bump_ContentVersion_Not_StructureVersion()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService());

        var id = new TableIdentifier("sales", "dbo", "customers");
        var initial = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                TableName = id.TableName,
                SchemaName = id.Schema,
                SourceName = id.Domain,
                DisplayName = "Customers",
                Description = "Customer records",
                SourceType = "sqlite"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        await repo.UpsertTableMetadataAsync(initial);

        var v1 = await repo.GetCatalogVersionAsync();
        v1.StructureVersion.ShouldBe(2); // Initial insert is structural, bumps 1 -> 2
        v1.ContentVersion.ShouldBe(1);

        // Update ONLY text metadata (DisplayName, Texts)
        var updatedTexts = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table
            {
                Id = initial.Table.Id,
                TableName = id.TableName,
                SchemaName = id.Schema,
                SourceName = id.Domain,
                DisplayName = "Kundenstamm",
                Description = "Kundenstammdaten und Adressen",
                SourceType = "sqlite",
                Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
                {
                    ["de"] = new("Kundenstamm", "Kundenstammdaten", null)
                })
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        await repo.UpsertTableMetadataAsync(updatedTexts);

        var v2 = await repo.GetCatalogVersionAsync();
        v2.StructureVersion.ShouldBe(2); // Structure did NOT bump! (Still 2)
        v2.ContentVersion.ShouldBe(2);   // Content DID bump! (1 -> 2)

        // Now update structure: add a new column
        var updatedStructure = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = updatedTexts.Table,
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ]
        };

        await repo.UpsertTableMetadataAsync(updatedStructure);

        var v3 = await repo.GetCatalogVersionAsync();
        v3.StructureVersion.ShouldBe(3); // Structure DID bump! (2 -> 3)
        v3.ContentVersion.ShouldBe(2);   // Content did NOT bump! (Still 2)
    }
}
