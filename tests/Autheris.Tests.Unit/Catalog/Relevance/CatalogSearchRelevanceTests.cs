namespace Autheris.Tests.Unit.Catalog.Relevance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using NSubstitute;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Relevance set of the catalog search (#624): a neutral sample catalog and queries with the expected tables and a maximum
/// rank. Each case names the feature it needs; cases whose feature is not built yet are skipped with the issue that builds it
/// (the <c>Skip</c> on the matching <see cref="RelevanceCasesAttribute"/>). The test output names the rank of every expected
/// table, so a ranking change shows which cases it improves.
/// </summary>
public sealed class CatalogSearchRelevanceTests(ITestOutputHelper output)
{
    /// <summary>Hits requested per query: the whole fixture, so the report can name ranks beyond <c>maxRank</c>.</summary>
    private static int RankedHits => RelevanceFixture.Tables.Count;

    /// <summary>The issue rows of #624 that need at least two queries each.</summary>
    private static readonly string[] IssueRows =
        ["field-weights", "partial-words", "word-forms", "cross-language", "related-term", "full-identifier"];

    [Theory]
    [RelevanceCases(RelevanceNeed.Baseline)]
    [RelevanceCases(RelevanceNeed.FieldWeights, Skip = "Needs per-field weights in the search index (#625).")]
    [RelevanceCases(RelevanceNeed.PartialWords, Skip = "Needs partial-word matching (#626).")]
    [RelevanceCases(RelevanceNeed.WordForms, Skip = "Needs word forms (stemming); not planned, owner decision Q-SRCH-6 (#624).")]
    [RelevanceCases(RelevanceNeed.Embeddings, Skip = "Needs a multilingual embedding model (#628).")]
    [RelevanceCases(RelevanceNeed.FullIdentifier, Skip = "Needs the full dataset id in search_catalog hits (#627).")]
    public async Task Case_ExpectedTablesWithinMaxRank(string caseId, string mode)
    {
        var c = RelevanceFixture.Case(caseId);

        var ranked = await RankedIdsAsync(c.Query, mode);
        var ranks = RelevanceFixture.Ranks(c, ranked);
        output.WriteLine(RelevanceFixture.Describe(c, mode, ranks));

        foreach (var (table, rank) in ranks)
        {
            rank.ShouldNotBeNull($"{c.Id} [{mode}]: {table} is not among the hits for '{c.Query}'.");
            rank.Value.ShouldBeLessThanOrEqualTo(c.MaxRank, $"{c.Id} [{mode}]: {table} ranks {rank} for '{c.Query}'.");
        }

        if (c.Needs == RelevanceNeed.FullIdentifier)
        {
            // Distinguishable for the client: search_catalog names every expected table by its full dataset id.
            var mcpTables = await SearchCatalogTablesAsync(c.Query, mode);
            output.WriteLine($"search_catalog tables: {string.Join(", ", mcpTables)}");
            mcpTables.ShouldBe(mcpTables.Distinct(StringComparer.Ordinal).ToList(), $"{c.Id} [{mode}]: two hits share one name.");
            foreach (var table in c.Expected)
            {
                mcpTables.ShouldContain(table, $"{c.Id} [{mode}]: search_catalog does not name {table}.");
            }
        }
    }

    /// <summary>
    /// The report of the whole set, including cases whose feature is not built: one line per case, mode and expected table.
    /// It asserts only that every expected table exists, so it stays green while it reports.
    /// </summary>
    [Fact]
    public async Task AllCases_ReportRankPerExpectedTable()
    {
        var known = RelevanceFixture.Tables.Select(t => t.Identifier.ToString()).ToHashSet(StringComparer.Ordinal);
        var report = new StringBuilder();

        foreach (var c in RelevanceFixture.Cases)
        {
            c.Expected.ShouldAllBe(id => known.Contains(id), $"{c.Id} expects a table that is not in the fixture.");
            foreach (var mode in RelevanceMode.All)
            {
                var ranks = RelevanceFixture.Ranks(c, await RankedIdsAsync(c.Query, mode));
                var met = ranks.Values.All(r => r != null && r <= c.MaxRank);
                var line = RelevanceFixture.Describe(c, mode, ranks);
                if (c.Needs == RelevanceNeed.FullIdentifier)
                {
                    var mcpTables = await SearchCatalogTablesAsync(c.Query, mode);
                    met &= mcpTables.Distinct(StringComparer.Ordinal).Count() == mcpTables.Count && c.Expected.All(mcpTables.Contains);
                    line += $"; search_catalog tables: {string.Join(", ", mcpTables)}";
                }

                report.AppendLine($"{(met ? "MET    " : "NOT MET")} {line}");
            }
        }

        output.WriteLine(report.ToString());
    }

    [Fact]
    public void Fixture_ContainsSameNameInTwoSchemas()
    {
        var sameName = RelevanceFixture.Tables
            .GroupBy(t => (t.Identifier.Domain, t.Identifier.TableName))
            .Where(g => g.Select(t => t.Identifier.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
            .ToList();

        sameName.ShouldNotBeEmpty();
        RelevanceFixture.Cases
            .Where(c => c.Needs == RelevanceNeed.FullIdentifier)
            .ShouldAllBe(c => c.Expected.Count >= 2);
    }

    [Fact]
    public void Fixture_HasTheShapeAskedForInTheIssue()
    {
        var tables = RelevanceFixture.Tables;

        tables.Count.ShouldBeInRange(30, 50);
        tables.Select(t => t.Identifier).Distinct().Count().ShouldBe(tables.Count);
        tables.ShouldContain(t => t.Table.Texts.Entries.ContainsKey("en"), "English texts are missing.");
        tables.ShouldContain(t => t.Table.Texts.IsEmpty, "German-only tables are missing.");
        tables.ShouldContain(t => !string.IsNullOrWhiteSpace(t.Table.LongDescription), "Long descriptions are missing.");
        tables.ShouldAllBe(t => t.Columns.Count > 0 && t.Columns.All(c => !string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void EveryCaseNeedingAFeature_LinksAnIssue()
    {
        foreach (var c in RelevanceFixture.Cases)
        {
            RelevanceNeed.All.ShouldContain(c.Needs, $"{c.Id} needs an unknown feature: no test runs it.");
            c.Issue.ShouldBeGreaterThan(0, $"{c.Id} links no issue.");
            c.MaxRank.ShouldBeGreaterThan(0, c.Id);
            c.Expected.ShouldNotBeEmpty(c.Id);
            c.Note.ShouldNotBeNullOrWhiteSpace(c.Id);
        }

        RelevanceFixture.Cases.Select(c => c.Id).ShouldBeUnique();
    }

    [Fact]
    public void EveryIssueRow_HasAtLeastTwoQueries()
    {
        foreach (var row in IssueRows)
        {
            RelevanceFixture.Cases
                .Where(c => c.Row == row)
                .Select(c => c.Query)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
                .ShouldBeGreaterThanOrEqualTo(2, $"Row '{row}' of #624 needs at least two queries.");
        }

        RelevanceFixture.Cases.Select(c => c.Row).Distinct().ShouldBeSubsetOf(IssueRows.Append(RelevanceNeed.Baseline));
    }

    /// <summary>
    /// A term that occurs only in the description of a column the caller may not see never produces a hit for that caller,
    /// while an unrestricted administrator finds the table by the same term (GHSA-95f9-ffc7-vw2c invariants).
    /// </summary>
    [Theory]
    [Trait("Category", "Security")]
    [InlineData(CatalogSearchMode.KeywordBm25)]
    [InlineData(CatalogSearchMode.Hybrid)]
    public async Task RestrictedCaller_FixtureHiddenColumnText_NeverProducesHit(CatalogSearchMode mode)
    {
        const string hiddenTerm = "Tarifgruppe";
        const string sid = "S-RELEVANCE-USER";
        var tables = RelevanceFixture.Tables.ToArray();
        var employees = tables.Single(t => t.Identifier.ToString() == "hr.core.employees");
        tables.Where(t => t.Columns.Any(c => c.Description?.Contains(hiddenTerm, StringComparison.OrdinalIgnoreCase) == true))
            .Select(t => t.Identifier)
            .ShouldBe([employees.Identifier], "The hidden term must occur in one column of one table only.");

        var engine = await RelevanceFixture.EngineAsync(mode == CatalogSearchMode.Hybrid ? RelevanceMode.Hybrid : RelevanceMode.Keyword);
        var visibleColumns = employees.Columns.Select(c => c.ColumnName).Where(n => n != "salary_band").ToArray();
        var restricted = Wp0.Service(
            Wp0.Tables(tables),
            Wp0.Consents(Wp0.Allow(employees.Identifier, sid, Wp0.TenantA, visibleColumns)),
            Wp0.Options(TenancyShape.Multi),
            engine);
        var restrictedContext = Wp0.Context(Wp0.User(sid, "tenant-a"));

        var hidden = await restricted.SearchCatalogDetailedAsync(new CatalogSearchQuery(hiddenTerm, Mode: mode, Limit: 10), restrictedContext);
        var visible = await restricted.SearchCatalogDetailedAsync(new CatalogSearchQuery("Mitarbeiterstamm", Mode: mode, Limit: 10), restrictedContext);

        hidden.ShouldBeEmpty();
        JsonSerializer.Serialize(visible).ShouldNotContain("salary_band");
        visible.Select(h => h.TableIdentifier).ShouldContain(employees.Identifier);

        var admin = Wp0.Service(Wp0.Tables(tables), Wp0.Consents(), Wp0.Options(TenancyShape.Single), engine);
        var adminHits = await admin.SearchCatalogDetailedAsync(
            new CatalogSearchQuery(hiddenTerm, Mode: mode, Limit: 10),
            Wp0.Context(Wp0.User("S-RELEVANCE-ADMIN", null, ["GovernanceAdmin"])));
        adminHits.Select(h => h.TableIdentifier).ShouldContain(employees.Identifier);
    }

    private static async Task<IReadOnlyList<string>> RankedIdsAsync(string query, string mode)
    {
        var engine = await RelevanceFixture.EngineAsync(mode);
        var hits = await engine.SearchAsync(
            new CatalogSearchQuery(query, Limit: RankedHits, Mode: RelevanceFixture.SearchMode(mode)),
            _ => true);
        return hits.Select(h => h.TableIdentifier.ToString()).ToList();
    }

    /// <summary>The <c>table</c> values of a <c>search_catalog</c> answer over the engine's hits (the MCP projection only).</summary>
    private static async Task<IReadOnlyList<string>> SearchCatalogTablesAsync(string query, string mode)
    {
        var engine = await RelevanceFixture.EngineAsync(mode);
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => engine.SearchAsync(ci.Arg<CatalogSearchQuery>(), _ => true, ci.Arg<CancellationToken>()));

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            discovery,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<ITableMetadataRepository>());
        var tool = new McpToolDefinition(McpDatasetTools.SearchCatalog, "Search catalog", "{}", TargetGraphQLOperation: "search_catalog");
        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

        var json = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { query, mode, limit = McpToolExecutionHandler.MaxCatalogSearchResults }),
            session);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("datasets").EnumerateArray()
            .Select(d => d.GetProperty("table").GetString() ?? string.Empty)
            .ToList();
    }
}
