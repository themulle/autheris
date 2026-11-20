namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogSearchEngineTests
{
    private readonly LocalDeterministicEmbeddingGenerator _embedder = new();
    private readonly IOptions<CatalogSearchOptions> _options = Options.Create(new CatalogSearchOptions());

    [Fact]
    public void Search_WhenIndexNotBuilt_ThrowsCatalogSearchIndexNotReadyException()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        var query = new CatalogSearchQuery("customer");

        Should.Throw<CatalogSearchIndexNotReadyException>(() =>
        {
            engine.Search(query, _ => true);
        });
    }

    [Fact]
    public async Task Search_HybridMode_ReturnsRankedHitsViaRrf()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);

        var table1 = CreateTable("sales", "orders", "Kundenbestellungen und Lieferungen");
        var table2 = CreateTable("finance", "invoices", "Rechnungen und Zahlungsabgleiche");
        var table3 = CreateTable("crm", "customers", "Kundenstammdaten und Adressen");

        await engine.RebuildIndexAsync([table1, table2, table3]);

        engine.IsIndexReady.ShouldBeTrue();
        engine.IndexedTableCount.ShouldBe(3);

        var hits = engine.Search(new CatalogSearchQuery("rechnungen faktura", Mode: CatalogSearchMode.Hybrid), _ => true);

        hits.ShouldNotBeEmpty();
        hits[0].TableIdentifier.ShouldBe(table2.Identifier);
        hits[0].CombinedScore.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public async Task Search_EnforcesVisibility_HidesUnauthorizedTables()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);

        var publicTable = CreateTable("crm", "customers", "Kundenstamm");
        var secretTable = CreateTable("hr", "salaries", "Vorstandsgehälter und Boni");

        await engine.RebuildIndexAsync([publicTable, secretTable]);

        // Principal hat nur Zugriff auf crm.customers
        var hits = engine.Search(
            new CatalogSearchQuery("gehälter boni executive", Mode: CatalogSearchMode.Hybrid),
            tableId => tableId == publicTable.Identifier
        );

        // secretTable darf unter keinen Umständen auftauchen
        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_ExpandsFirstDegreeForeignKeys_ReturnsJoinConditions()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);

        var custId = new TableIdentifier("sales", "public", "customers");
        var orderId = new TableIdentifier("sales", "public", "orders");

        var custTable = new TableMetadata
        {
            Identifier = custId,
            Table = new Table { Description = "Kundenstamm", Sensitivity = "NORMAL" },
            Columns = [new TableColumn { ColumnName = "customer_id", DataType = "int" }],
            PrimaryKeyColumns = ["customer_id"]
        };

        var orderTable = new TableMetadata
        {
            Identifier = orderId,
            Table = new Table { Description = "Bestellungen", Sensitivity = "NORMAL" },
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "int" },
                new TableColumn { ColumnName = "customer_id", DataType = "int" }
            ],
            PrimaryKeyColumns = ["order_id"]
        };

        await engine.RebuildIndexAsync([custTable, orderTable]);

        var hits = engine.Search(
            new CatalogSearchQuery("bestellungen orders", Limit: 5, ExpandRelations: true),
            _ => true);

        hits.ShouldNotBeEmpty();
        var orderHit = hits.FirstOrDefault(h => h.TableIdentifier == orderId);
        orderHit.ShouldNotBeNull();
        orderHit.RelatedJoinPaths.ShouldNotBeEmpty();
        orderHit.RelatedJoinPaths[0].ToTable.TableName.ShouldBe("customers");
        orderHit.RelatedJoinPaths[0].FromColumn.ShouldBe("customer_id");
        orderHit.RelatedJoinPaths[0].ToColumn.ShouldBe("customer_id");
    }

    [Fact]
    public async Task Search_FiltersByDomain_WhenDomainProvided()
    {
        var engine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);

        var salesOrders = CreateTable("sales", "orders", "Bestellungen");
        var financeOrders = CreateTable("finance", "orders", "Finanz-Bestellungen");

        await engine.RebuildIndexAsync([salesOrders, financeOrders]);

        var hits = engine.Search(
            new CatalogSearchQuery("orders", DomainFilter: "finance"),
            _ => true);

        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(h => h.Domain == "finance");
    }

    [Fact]
    public async Task ApplyDelta_SingleTableUpdate_CostIsIndependentOfCatalogSize()
    {
        var smallTables = Enumerable.Range(0, 500)
            .Select(i => CreateTable("sales", $"small_t_{i:D5}", $"Description for small table {i}"))
            .ToList();
        var largeTables = Enumerable.Range(0, 10_000)
            .Select(i => CreateTable("sales", $"large_t_{i:D5}", $"Description for large table {i} with distinct token tok{i}"))
            .ToList();

        var smallEngine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        await smallEngine.RebuildIndexAsync(smallTables);

        var largeEngine = new CatalogSearchEngine(_embedder, _options, NullLogger<CatalogSearchEngine>.Instance);
        await largeEngine.RebuildIndexAsync(largeTables);

        // Warm up JIT for ApplyDeltaAsync on the small engine.
        var smallUpdate = smallTables[10] with { Table = smallTables[10].Table with { Description = "warmed small delta" } };
        await smallEngine.ApplyDeltaAsync([smallUpdate], []);

        var largeUpdate = largeTables[5_000] with { Table = largeTables[5_000].Table with { Description = "updated large delta" } };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await largeEngine.ApplyDeltaAsync([largeUpdate], []);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(100);
        largeEngine.IndexedTableCount.ShouldBe(10_000);

        var hits = largeEngine.Search(new CatalogSearchQuery("updated large delta"), _ => true);
        hits.ShouldContain(h => h.TableIdentifier == largeUpdate.Identifier);
    }

    private static TableMetadata CreateTable(string domain, string tableName, string description) =>
        new()
        {
            Identifier = new TableIdentifier(domain, "public", tableName),
            Table = new Table { Description = description, Sensitivity = "NORMAL" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
}
