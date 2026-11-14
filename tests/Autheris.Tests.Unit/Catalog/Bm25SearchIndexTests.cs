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

    private static readonly TableIdentifier Master = TableIdentifier.Parse("erp.master_crane");
    private static readonly TableIdentifier Telemetry = TableIdentifier.Parse("iot.telemetry_a");

    [Fact]
    public void TermInTechnicalName_OutranksTermOnlyInColumnDescriptions()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp master_crane", ColumnNames: "id name"),
            new(Telemetry, TechnicalName: "iot telemetry_a", ColumnNames: "crane_ref", ColumnDescriptions: "crane id of the crane crane"),
            new(TableIdentifier.Parse("iot.telemetry_b"), TechnicalName: "iot telemetry_b", ColumnDescriptions: "crane id"),
        };

        var results = new Bm25SearchIndex(docs).Search("crane", topK: 5);

        results[0].TableId.ShouldBe(Master);
        results.Count.ShouldBe(3);
    }

    [Fact]
    public void FieldWeightConstants_ChangeRanking()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp master_crane"),
            new(Telemetry, TechnicalName: "iot telemetry_a", ColumnDescriptions: "crane"),
            new(TableIdentifier.Parse("iot.other"), TechnicalName: "iot other", ColumnDescriptions: "filler text here"),
        };

        var defaults = new Bm25SearchIndex(docs).Search("crane");
        defaults[0].TableId.ShouldBe(Master);

        // Column descriptions weighted far above the technical name flips the order.
        var flipped = new Bm25SearchIndex(docs, [0.1, 8, 5, 3, 2, 2, 50]).Search("crane");
        flipped[0].TableId.ShouldBe(Telemetry);
    }

    [Fact]
    public void PerFieldLengthNormalization_LongDescriptionDoesNotDominate()
    {
        var longText = string.Join(' ', Enumerable.Repeat("filler", 200)) + " crane";
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp master", LongDescription: longText),
            new(Telemetry, TechnicalName: "iot telemetry", LongDescription: "crane"),
        };

        var results = new Bm25SearchIndex(docs).Search("crane");

        results[0].TableId.ShouldBe(Telemetry);
        results[0].Score.ShouldBeGreaterThan(results[1].Score);
    }

    [Fact]
    public void ZeroLengthField_DoesNotProduceNaN()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "crane"),
            new(Telemetry, ShortDescription: "   "),
        };

        var results = new Bm25SearchIndex(docs).Search("crane");

        results.Count.ShouldBe(1);
        double.IsFinite(results[0].Score).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidFieldWeight_IsRejected(double bad)
    {
        Should.Throw<ArgumentException>(() => new Bm25SearchIndex([], [bad, 1, 1, 1, 1, 1, 1]));
        Should.Throw<ArgumentException>(() => new Bm25SearchIndex([], [1.0, 1.0]));
    }

    [Fact]
    public void ColumnRestrictedTable_ScoresFromTableLevelFieldsOnly()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp master", ColumnNames: "crane_secret", ColumnDescriptions: "crane"),
            new(Telemetry, TechnicalName: "iot telemetry", ShortDescription: "crane data"),
        };
        var index = new Bm25SearchIndex(docs);

        index.Search("crane").Select(r => r.TableId).ShouldContain(Master);

        var restricted = index.Search("crane", isColumnRestricted: id => id == Master);
        restricted.Select(r => r.TableId).ShouldBe([Telemetry]);
    }
}
