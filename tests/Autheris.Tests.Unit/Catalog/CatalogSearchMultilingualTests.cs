namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogSearchMultilingualTests
{
    private readonly LocalDeterministicEmbeddingGenerator _embedder = new();
    private readonly IOptions<CatalogSearchOptions> _options = Options.Create(new CatalogSearchOptions());

    private static TableMetadata CreateMultilingualTable()
    {
        var id = new TableIdentifier("sales", "dbo", "orders");
        var tableTexts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new("Kundenaufträge", "Alle Kundenbestellungen und Lieferungen für Großkunden", "Detaillierte Historie aller Bestellungen."),
            ["en"] = new("Customer Orders", "All client orders and shipments for wholesale customers", "Detailed history of all orders.")
        });

        var columnTexts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new("Gesamtbetrag", "Rechnungssumme inklusive Mehrwertsteuer", null),
            ["en"] = new("Total Amount", "Invoice total including value added tax", null)
        });

        return new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                TableName = id.TableName,
                SchemaName = id.Schema,
                SourceName = id.Domain,
                DisplayName = "Orders",
                Description = "Default orders table description",
                Texts = tableTexts
            },
            Columns =
            [
                new TableColumn
                {
                    ColumnName = "id",
                    DataType = "integer",
                    DisplayName = "ID",
                    Description = "Primary key"
                },
                new TableColumn
                {
                    ColumnName = "total_amount",
                    DataType = "numeric",
                    DisplayName = "Total Amount",
                    Description = "Total price",
                    Texts = columnTexts
                }
            ]
        };
    }

    [Fact]
    public async Task Search_GermanQuery_MatchesGermanTexts_AndResolvesGermanMetadata()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        var table = CreateMultilingualTable();
        await engine.RebuildIndexAsync([table]);

        var query = new CatalogSearchQuery("Großkunden Lieferungen", Language: "de");
        var hits = engine.Search(query, _ => true);

        hits.ShouldNotBeEmpty();
        var hit = hits[0];
        hit.TableIdentifier.ShouldBe(table.Identifier);
        hit.DisplayName.ShouldBe("Kundenaufträge");
        hit.Description.ShouldBe("Alle Kundenbestellungen und Lieferungen für Großkunden");
        hit.Language.ShouldBe("de");
    }

    [Fact]
    public async Task Search_EnglishQuery_MatchesEnglishTexts_AndResolvesEnglishMetadata()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        var table = CreateMultilingualTable();
        await engine.RebuildIndexAsync([table]);

        var query = new CatalogSearchQuery("wholesale shipments", Language: "en");
        var hits = engine.Search(query, _ => true);

        hits.ShouldNotBeEmpty();
        var hit = hits[0];
        hit.TableIdentifier.ShouldBe(table.Identifier);
        hit.DisplayName.ShouldBe("Customer Orders");
        hit.Description.ShouldBe("All client orders and shipments for wholesale customers");
        hit.Language.ShouldBe("en");
    }

    [Fact]
    public async Task Search_DiacriticVariant_MatchesUmlautAndEszett()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        var table = CreateMultilingualTable();
        await engine.RebuildIndexAsync([table]);

        // Search with ss ("grosskunden") against text with ß ("Großkunden")
        var query = new CatalogSearchQuery("grosskunden lieferungen", Language: "de");
        var hits = engine.Search(query, _ => true);

        hits.ShouldNotBeEmpty();
        hits[0].TableIdentifier.ShouldBe(table.Identifier);
    }

    [Fact]
    public async Task Search_ColumnLocalizedText_IsFoundInSearch()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        var table = CreateMultilingualTable();
        await engine.RebuildIndexAsync([table]);

        var query = new CatalogSearchQuery("Mehrwertsteuer", Language: "de");
        var hits = engine.Search(query, _ => true);

        hits.ShouldNotBeEmpty();
        hits[0].TableIdentifier.ShouldBe(table.Identifier);
        hits[0].RelevantColumns.ShouldContain("total_amount");
    }
}
