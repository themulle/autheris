namespace Autheris.Tests.Unit.Catalog;

using System.Collections.Generic;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class Bm25SearchIndexTests
{
    [Fact]
    public void Search_EmptyIndex_ReturnsEmptyList()
    {
        var index = new Bm25SearchIndex([]);
        var results = index.Search("customer");
        results.ShouldBeEmpty();
    }

    [Fact]
    public void Search_MatchesExactAcronym_AndRanksHighest()
    {
        var table1 = new TableIdentifier("erp", "sap", "tbl_kna1_customer_master");
        var table2 = new TableIdentifier("sales", "analytics", "fct_orders");
        var table3 = new TableIdentifier("finance", "gl", "general_ledger_entries");

        var docs = new List<(TableIdentifier, string)>
        {
            (table1, "erp sap tbl_kna1_customer_master Debitorenstammdaten Kundennummer KNA1"),
            (table2, "sales analytics fct_orders Verkaufsaufträge Bestellungen"),
            (table3, "finance gl general_ledger_entries Buchungen Hauptbuch")
        };

        var index = new Bm25SearchIndex(docs);

        var results = index.Search("kna1 debitor", topK: 5);

        results.ShouldNotBeEmpty();
        results[0].TableId.ShouldBe(table1);
        results[0].Score.ShouldBeGreaterThan(0.0);
        results[0].MatchedTerms.ShouldContain("kna1");
    }

    [Fact]
    public void Search_MatchesAbbreviations_ViaExpansion()
    {
        var table1 = new TableIdentifier("billing", "public", "tbl_inv_head");
        var table2 = new TableIdentifier("crm", "public", "customers");

        var docs = new List<(TableIdentifier, string)>
        {
            (table1, "billing tbl_inv_head invoice headers"),
            (table2, "crm customers customer records")
        };

        var index = new Bm25SearchIndex(docs);

        var results = index.Search("invoice", topK: 5);

        results.ShouldNotBeEmpty();
        results[0].TableId.ShouldBe(table1);
    }
}
