namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void Betriebsstunden_FindsMotorbetriebsstunden()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp engine_hours", ShortDescription: "Motorbetriebsstunden je Fahrzeug"),
            new(Telemetry, TechnicalName: "iot telemetry", ShortDescription: "Positionsdaten"),
        };

        var results = new Bm25SearchIndex(docs).Search("Betriebsstunden");

        results.Select(r => r.TableId).ShouldBe([Master]);
        results[0].MatchedTerms.ShouldContain("motorbetriebsstunden");
    }

    [Fact]
    public void Kran_FindsKranspezifikationen()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp model_series", LongDescription: "Kranspezifikationen, Krantyps und Kranbaureihen"),
            new(Telemetry, TechnicalName: "iot telemetry", ShortDescription: "Positionsdaten"),
        };

        new Bm25SearchIndex(docs).Search("Kran").Select(r => r.TableId).ShouldBe([Master]);
    }

    [Fact]
    public void WholeWord_OutranksPartialMatch()
    {
        var whole = TableIdentifier.Parse("erp.whole");
        var partial = TableIdentifier.Parse("erp.partial");
        var docs = new List<Bm25Document>
        {
            new(partial, TechnicalName: "erp partial", TagsAndOwner: "", ShortDescription: "Kranspezifikationen"),
            new(whole, TechnicalName: "erp whole", ShortDescription: "Kran"),
            new(Telemetry, TechnicalName: "iot telemetry", ShortDescription: "Positionsdaten"),
        };

        var results = new Bm25SearchIndex(docs).Search("Kran");

        results.Select(r => r.TableId).ShouldBe([whole, partial]);
        results[0].Score.ShouldBeGreaterThan(results[1].Score);
    }

    [Fact]
    public void TokenShorterThan4_MatchesWholeWordsOnly()
    {
        var docs = new List<Bm25Document>
        {
            new(Master, TechnicalName: "erp master", ShortDescription: "Kranbau"),
            new(Telemetry, TechnicalName: "iot telemetry", ShortDescription: "kra"),
        };
        var index = new Bm25SearchIndex(docs);

        index.Search("kra").Select(r => r.TableId).ShouldBe([Telemetry]);
        index.Search("kran").Select(r => r.TableId).ShouldBe([Master]);
    }

    [Fact]
    public void ExpansionCap_BoundsCandidateTerms()
    {
        var docs = Enumerable.Range(0, Bm25SearchIndex.MaxPartialExpansions + 10)
            .Select(i => new Bm25Document(TableIdentifier.Parse($"erp.t{i}"), ShortDescription: $"kran{i:D3}"))
            .ToList();

        var results = new Bm25SearchIndex(docs).Search("kran", topK: 1000);

        results.Count.ShouldBe(Bm25SearchIndex.MaxPartialExpansions);
    }

    [Fact]
    public void QueryWith1000Tokens_IsBoundedAndFast()
    {
        var docs = Enumerable.Range(0, 2000)
            .Select(i => new Bm25Document(TableIdentifier.Parse($"erp.t{i}"), ShortDescription: $"alpha{i} beta{i} gamma{i}"))
            .ToList();
        var index = new Bm25SearchIndex(docs);
        var query = string.Join(' ', Enumerable.Range(0, 1000).Select(i => $"alpha{i}"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = index.Search(query, topK: 5000);
        sw.Stop();

        results.Count.ShouldBeLessThanOrEqualTo(Bm25SearchIndex.MaxQueryTerms * (Bm25SearchIndex.MaxPartialExpansions + 1));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Expansion_UsesTurkishCultureInvariantFolding()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var docs = new List<Bm25Document>
            {
                new(Master, TechnicalName: "erp master", ShortDescription: "SUBTITLE of INFORMATION"),
                new(Telemetry, TechnicalName: "iot telemetry", ShortDescription: "Positionsdaten"),
            };

            new Bm25SearchIndex(docs).Search("TITLE").Select(r => r.TableId).ShouldBe([Master]);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
