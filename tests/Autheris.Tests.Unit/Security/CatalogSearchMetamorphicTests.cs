namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

/// <summary>
/// Metamorphic testing suite for Catalog Search, Smart Schema Tokenization, Diacritic Normalization,
/// BM25 ranking, and Pre-filter Visible BitSets.
/// Validates mathematical invariants: Term reinforcement monotonicity, Diacritic symmetry,
/// Pre-filter candidate isolation, Permutation invariance of table set SHA-256 digests,
/// and BitSet popcount monotonicity.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "Metamorphic")]
public sealed class CatalogSearchMetamorphicTests
{
    #region 1. BM25 Ranking Metamorphic Invariants

    [Fact]
    public void M_SEARCH_01A_TermRepetition_IsIdempotentDueToQueryDeduplication()
    {
        // Invariant: Because SmartSchemaTokenizer deduplicates query terms, repeating an existing term
        // is strictly idempotent: Score(Q + term) == Score(Q) when term already exists in Q.
        var t1 = TableIdentifier.Parse("erp.customer_invoices");
        var t2 = TableIdentifier.Parse("crm.customer_contacts");

        var corpus = new[]
        {
            (t1, "customer invoices and financial billing records for accounting"),
            (t2, "customer contacts addresses and email phone numbers")
        };

        var index = new Bm25SearchIndex(corpus);

        var resultsQ1 = index.Search("customer invoices", topK: 10);
        resultsQ1.ShouldNotBeEmpty();
        resultsQ1[0].TableId.ShouldBe(t1);
        double baselineScoreT1 = resultsQ1[0].Score;

        var resultsQ2 = index.Search("customer invoices invoices", topK: 10);
        resultsQ2.ShouldNotBeEmpty();
        resultsQ2[0].TableId.ShouldBe(t1);
        double repeatedScoreT1 = resultsQ2[0].Score;

        repeatedScoreT1.ShouldBe(baselineScoreT1);
    }

    [Fact]
    public void M_SEARCH_01B_DistinctMatchingTermAddition_StrictlyIncreasesScore()
    {
        // Invariant: Appending a distinct new term present in document A (and not present in existing query)
        // adds an IDF component, strictly increasing document A's score.
        var t1 = TableIdentifier.Parse("erp.customer_invoices");
        var t2 = TableIdentifier.Parse("crm.customer_contacts");

        var corpus = new[]
        {
            (t1, "customer invoices and financial billing records for accounting"),
            (t2, "customer contacts addresses and email phone numbers")
        };

        var index = new Bm25SearchIndex(corpus);

        var resultsQ1 = index.Search("customer invoices", topK: 10);
        resultsQ1[0].TableId.ShouldBe(t1);
        double baselineScoreT1 = resultsQ1[0].Score;

        // 'billing' is an additional distinct matching term in t1
        var resultsQ2 = index.Search("customer invoices billing", topK: 10);
        resultsQ2[0].TableId.ShouldBe(t1);
        double expandedScoreT1 = resultsQ2[0].Score;

        expandedScoreT1.ShouldBeGreaterThan(baselineScoreT1);
    }

    [Fact]
    public void M_SEARCH_02_IrrelevantTermAddition_PreservesZeroScoreOnUnmatchedDocs()
    {
        // Invariant: Adding terms that do not appear anywhere in the corpus must not alter existing scores
        // or cause unmatched documents to receive a non-zero score.
        var t1 = TableIdentifier.Parse("sales.orders");
        var t2 = TableIdentifier.Parse("hr.employees");

        var corpus = new[]
        {
            (t1, "sales orders order items transactions"),
            (t2, "hr employees salaries departments")
        };

        var index = new Bm25SearchIndex(corpus);

        var r1 = index.Search("orders", topK: 10);
        r1.Count.ShouldBe(1);
        r1[0].TableId.ShouldBe(t1);

        // Term "xyznonexistentterm" does not exist in any document
        var r2 = index.Search("orders xyznonexistentterm", topK: 10);
        r2.Count.ShouldBe(1);
        r2[0].TableId.ShouldBe(t1);
        r2[0].Score.ShouldBe(r1[0].Score);
    }

    #endregion

    #region 2. Diacritic Normalization & Symmetry Invariants

    [Theory]
    [InlineData("Übergrößen", "uebergroessen")]
    [InlineData("Aufträge", "auftraege")]
    [InlineData("Müller", "mueller")]
    [InlineData("KÖNIG", "koenig")]
    [InlineData("Straße", "strasse")]
    public void M_SEARCH_03_DiacriticNormalization_GermanUmlauts_FoldsConsistently(string input, string expected)
    {
        // Invariant: German umlauts and sharp-s fold deterministically to lowercase ASCII expansions
        var folded = DiacriticNormalizer.NormalizeAndFold(input);
        folded.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Crème Brûlée", "creme brulee")]
    [InlineData("Kraków", "krakow")]
    [InlineData("São Paulo", "sao paulo")]
    [InlineData("Zürich", "zuerich")] // Umlaut expanded first then stripped
    public void M_SEARCH_04_DiacriticNormalization_Accents_StripsToAsciiEquivalents(string input, string expected)
    {
        // Invariant: Unicode Form D decomposition strips non-spacing combining marks
        var normalized = DiacriticNormalizer.NormalizeAndFold(input);
        normalized.ShouldBe(expected);
    }

    [Fact]
    public void M_SEARCH_05_DiacriticSymmetry_Bm25SearchFindsDocumentsAcrossUmlautVariants()
    {
        // Invariant: Searching with umlauts or with expanded 'ae/oe/ue' returns the exact same document
        var t1 = TableIdentifier.Parse("finance.erloes_konten");
        var corpus = new[]
        {
            (t1, "Finanzkonten und Erlöskonten für Rechnungslegung und Erträge")
        };

        var index = new Bm25SearchIndex(corpus);

        var searchWithUmlaut = index.Search("erlöskonten", topK: 5);
        var searchWithAe = index.Search("erloeskonten", topK: 5);

        searchWithUmlaut.ShouldNotBeEmpty();
        searchWithAe.ShouldNotBeEmpty();
        searchWithUmlaut[0].TableId.ShouldBe(t1);
        searchWithAe[0].TableId.ShouldBe(t1);
    }

    #endregion

    #region 3. Pre-Filter Candidate Isolation & Relative Order Invariance

    [Fact]
    public void M_SEARCH_06_PreFilter_ExcludesDisallowedCandidates_AndPreservesRelativeOrder()
    {
        // Invariant: For any subset of allowed tables S, filtering search results by S must:
        // 1. Never include any table T not in S (Strict Zero-Leakage).
        // 2. Preserve the relative ranking order of allowed tables (If Rank(A) < Rank(B) in full corpus,
        //    then Rank(A) < Rank(B) in filtered results).
        var t1 = TableIdentifier.Parse("erp.customers_top");
        var t2 = TableIdentifier.Parse("erp.customers_mid");
        var t3 = TableIdentifier.Parse("erp.customers_low");
        var tSecret = TableIdentifier.Parse("secret.customers_confidential");

        var corpus = new[]
        {
            (t1, "customer customer customer global VIP accounts"),
            (t2, "customer customer standard business accounts"),
            (t3, "customer small retail accounts"),
            (tSecret, "customer confidential highly classified corporate accounts")
        };

        var index = new Bm25SearchIndex(corpus);

        // Unfiltered search establishes relative ordering: t1 > t2 > t3
        var fullResults = index.Search("customer", topK: 10);
        fullResults.Count.ShouldBe(4);

        // Filter allowing only t1 and t3, strictly excluding t2 and tSecret
        var allowedSet = new HashSet<TableIdentifier> { t1, t3 };
        var filteredResults = index.Search("customer", topK: 10, isAllowed: id => allowedSet.Contains(id));

        // 1. Candidates outside allowedSet must NEVER appear
        filteredResults.Any(r => r.TableId == t2).ShouldBeFalse();
        filteredResults.Any(r => r.TableId == tSecret).ShouldBeFalse();

        // 2. Relative order of allowed tables must be preserved
        filteredResults.Count.ShouldBe(2);
        filteredResults[0].TableId.ShouldBe(t1);
        filteredResults[1].TableId.ShouldBe(t3);
    }

    #endregion

    #region 4. Catalog Visible BitSet Invariants

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(256)]
    public void M_SEARCH_07_CatalogVisibleBitSet_AllSet_HasExactCapacityAndPopCount(int capacity)
    {
        // Invariant: AllSet(C) must have SetCount == C, with bits [0..C-1] true, and bounds checks intact.
        var bitset = CatalogVisibleBitSet.AllSet(capacity);
        bitset.Capacity.ShouldBe(capacity);
        bitset.SetCount.ShouldBe(capacity);

        for (int i = 0; i < capacity; i++)
        {
            bitset.IsSet(i).ShouldBeTrue();
        }

        // Out-of-bounds queries fail closed (false)
        bitset.IsSet(capacity).ShouldBeFalse();
        bitset.IsSet(capacity + 1).ShouldBeFalse();
    }

    [Fact]
    public void M_SEARCH_08_CatalogVisibleBitSet_PopCount_IsMonotonicAndIdempotent()
    {
        // Invariant: Setting an already-set bit is idempotent (count does not change).
        // Setting an unset bit increments count by exactly 1.
        var bitset = new CatalogVisibleBitSet(100);
        bitset.SetCount.ShouldBe(0);

        bitset.Set(10);
        bitset.SetCount.ShouldBe(1);
        bitset.IsSet(10).ShouldBeTrue();

        // Idempotence
        bitset.Set(10);
        bitset.SetCount.ShouldBe(1);

        bitset.Set(42);
        bitset.SetCount.ShouldBe(2);
        bitset.IsSet(42).ShouldBeTrue();

        bitset.Set(99);
        bitset.SetCount.ShouldBe(3);
        bitset.IsSet(99).ShouldBeTrue();

        // Idempotence again
        bitset.Set(42);
        bitset.SetCount.ShouldBe(3);
    }

    [Fact]
    public void M_SEARCH_09_CatalogVisibleBitSet_Sha256_PermutationInvariance()
    {
        // Invariant: Given a list of tables, computing the SHA-256 digest must yield the EXACT same
        // 64-character hex string for all permutations of that list (order independence).
        var tables = new List<TableIdentifier>
        {
            TableIdentifier.Parse("schema_a.users"),
            TableIdentifier.Parse("schema_b.orders"),
            TableIdentifier.Parse("schema_c.payments"),
            TableIdentifier.Parse("schema_d.inventory")
        };

        string canonicalHash = CatalogVisibleBitSet.ComputeSha256(tables);
        canonicalHash.Length.ShouldBe(64);

        // Test 10 pseudo-random permutations
        var rng = new Random(1337);
        for (int i = 0; i < 10; i++)
        {
            var shuffled = tables.OrderBy(_ => rng.Next()).ToList();
            string permutedHash = CatalogVisibleBitSet.ComputeSha256(shuffled);
            permutedHash.ShouldBe(canonicalHash);
        }
    }

    [Fact]
    public void M_SEARCH_10_CatalogVisibleBitSet_Sha256_DelimiterInjectionResistance()
    {
        // Invariant: Length-prefixed encoding prevents delimiter injection collisions:
        // e.g. "a;1:b" + "" vs "a" + "b".
        var set1 = new[]
        {
            TableIdentifier.Parse("db.a_b"),
            TableIdentifier.Parse("db.c")
        };

        var set2 = new[]
        {
            TableIdentifier.Parse("db.a"),
            TableIdentifier.Parse("db.b_c")
        };

        string hash1 = CatalogVisibleBitSet.ComputeSha256(set1);
        string hash2 = CatalogVisibleBitSet.ComputeSha256(set2);

        hash1.ShouldNotBe(hash2);
    }

    #endregion

    #region 5. Smart Schema Tokenizer Domain Abbreviations Recall

    [Theory]
    [InlineData("kna1", "customer")]
    [InlineData("kna1", "kunde")]
    [InlineData("vbak", "sales")]
    [InlineData("vbak", "auftrag")]
    [InlineData("vbap", "position")]
    [InlineData("inv", "rechnung")]
    [InlineData("rev", "umsatz")]
    [InlineData("usr", "benutzer")]
    public void M_SEARCH_11_SmartSchemaTokenizer_DomainAbbreviations_ExpandsToSynonyms(string abbr, string expectedSynonym)
    {
        // Invariant: Tokenizing enterprise/SAP abbreviations expands to cross-lingual synonyms
        var tokens = SmartSchemaTokenizer.Tokenize(abbr, expandAbbreviations: true);
        tokens.ShouldContain(expectedSynonym);
    }

    [Fact]
    public void M_SEARCH_12_SmartSchemaTokenizer_CasingSplit_IsConsistentAcrossNamingConventions()
    {
        // Invariant: snake_case, camelCase, PascalCase, and kebab-case produce identical normalized semantic tokens
        var snakeTokens = SmartSchemaTokenizer.Tokenize("customer_invoice_header", expandAbbreviations: false);
        var camelTokens = SmartSchemaTokenizer.Tokenize("customerInvoiceHeader", expandAbbreviations: false);
        var pascalTokens = SmartSchemaTokenizer.Tokenize("CustomerInvoiceHeader", expandAbbreviations: false);
        var kebabTokens = SmartSchemaTokenizer.Tokenize("customer-invoice-header", expandAbbreviations: false);

        snakeTokens.ShouldBe(["customer", "invoice", "header"]);
        camelTokens.ShouldBe(["customer", "invoice", "header"]);
        pascalTokens.ShouldBe(["customer", "invoice", "header"]);
        kebabTokens.ShouldBe(["customer", "invoice", "header"]);
    }

    #endregion
}
