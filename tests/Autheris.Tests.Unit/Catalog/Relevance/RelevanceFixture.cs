namespace Autheris.Tests.Unit.Catalog.Relevance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

/// <summary>One relevance case of <c>relevance-cases.json</c> (#624).</summary>
internal sealed record RelevanceCase(
    string Id,
    string Row,
    string Query,
    IReadOnlyList<string> Expected,
    int MaxRank,
    string Needs,
    int Issue,
    string Note);

/// <summary>Search feature a relevance case depends on; <see cref="Baseline"/> needs nothing beyond today's search.</summary>
internal static class RelevanceNeed
{
    public const string Baseline = "baseline";
    public const string FieldWeights = "field-weights";
    public const string PartialWords = "partial-words";
    public const string WordForms = "word-forms";
    public const string Embeddings = "embeddings";
    public const string FullIdentifier = "full-identifier";

    public static readonly IReadOnlyList<string> All = [Baseline, FieldWeights, PartialWords, WordForms, Embeddings, FullIdentifier];
}

/// <summary>Search modes the relevance set runs in.</summary>
internal static class RelevanceMode
{
    public const string Keyword = "keyword";
    public const string Hybrid = "hybrid";

    public static readonly IReadOnlyList<string> All = [Keyword, Hybrid];
}

/// <summary>
/// Loads the neutral sample catalog and the relevance cases (#624) and builds one search engine per mode over it.
/// Keyword mode runs without an embedding provider; hybrid mode uses the deterministic test embedding generator, which is
/// lexical (hashed tokens), so it checks the rank fusion but cannot stand in for a multilingual model (#628).
/// </summary>
internal static class RelevanceFixture
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Lazy<IReadOnlyList<TableMetadata>> LazyTables = new(LoadTables);
    private static readonly Lazy<IReadOnlyList<RelevanceCase>> LazyCases = new(LoadCases);
    private static readonly Lazy<Task<CatalogSearchEngine>> KeywordEngine = new(() => BuildEngineAsync(CatalogEmbeddingProvider.None));

    // Any provider other than None switches the engine to vector search; the generator itself is the deterministic test double.
    private static readonly Lazy<Task<CatalogSearchEngine>> HybridEngine = new(() => BuildEngineAsync(CatalogEmbeddingProvider.Ollama));

    public static IReadOnlyList<TableMetadata> Tables => LazyTables.Value;

    public static IReadOnlyList<RelevanceCase> Cases => LazyCases.Value;

    public static RelevanceCase Case(string id) =>
        Cases.SingleOrDefault(c => c.Id == id) ?? throw new InvalidOperationException($"Unknown relevance case '{id}'.");

    public static Task<CatalogSearchEngine> EngineAsync(string mode) => mode switch
    {
        RelevanceMode.Keyword => KeywordEngine.Value,
        RelevanceMode.Hybrid => HybridEngine.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static CatalogSearchMode SearchMode(string mode) =>
        mode == RelevanceMode.Hybrid ? CatalogSearchMode.Hybrid : CatalogSearchMode.KeywordBm25;

    /// <summary>The 1-based rank of each expected dataset id in <paramref name="rankedIds"/>, or null when it is not ranked.</summary>
    public static IReadOnlyDictionary<string, int?> Ranks(RelevanceCase c, IReadOnlyList<string> rankedIds) =>
        c.Expected.ToDictionary(
            id => id,
            id => rankedIds.Select((r, i) => (r, i)).Where(x => x.r == id).Select(x => (int?)(x.i + 1)).FirstOrDefault(),
            StringComparer.Ordinal);

    public static string Describe(RelevanceCase c, string mode, IReadOnlyDictionary<string, int?> ranks) =>
        $"{c.Id} [{mode}] '{c.Query}' (max rank {c.MaxRank}, needs {c.Needs}, #{c.Issue}): " +
        string.Join(", ", ranks.Select(r => $"{r.Key} rank {r.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}"));

    private static string PathOf(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Catalog", "Relevance", fileName);

    private static async Task<CatalogSearchEngine> BuildEngineAsync(CatalogEmbeddingProvider provider)
    {
        var engine = new CatalogSearchEngine(
            new LocalDeterministicEmbeddingGenerator(),
            Microsoft.Extensions.Options.Options.Create(new CatalogSearchOptions { EmbeddingProvider = provider }),
            NullLogger<CatalogSearchEngine>.Instance);
        await engine.RebuildIndexAsync(Tables).ConfigureAwait(false);
        return engine;
    }

    private static IReadOnlyList<RelevanceCase> LoadCases()
    {
        var file = JsonSerializer.Deserialize<CasesFile>(File.ReadAllText(PathOf("relevance-cases.json")), Json)
                   ?? throw new InvalidOperationException("relevance-cases.json is empty.");
        return file.Cases;
    }

    private static IReadOnlyList<TableMetadata> LoadTables()
    {
        var file = JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(PathOf("catalog-relevance-fixture.json")), Json)
                   ?? throw new InvalidOperationException("catalog-relevance-fixture.json is empty.");
        return file.Tables.Select(ToMetadata).ToList();
    }

    private static TableMetadata ToMetadata(FixtureTable t)
    {
        var parts = t.Id.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException($"Fixture table id '{t.Id}' is not domain.schema.table.");
        }

        var id = new TableIdentifier(parts[0], parts[1], parts[2]);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                DisplayName = t.DisplayName,
                Description = t.Description,
                LongDescription = t.LongDescription,
                Texts = t.En == null
                    ? LocalizedTexts.Empty
                    : new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["en"] = new(t.En.DisplayName, t.En.Description) }),
                Tags = t.Tags ?? [],
                IsActive = true
            },
            Columns = t.Columns.Select(c => new TableColumn
            {
                ColumnName = c.Name,
                DataType = c.Type,
                Description = c.Description,
                Texts = c.En == null
                    ? LocalizedTexts.Empty
                    : new LocalizedTexts(new Dictionary<string, LocalizedTextEntry> { ["en"] = new(null, c.En) })
            }).ToList()
        };
    }

    private sealed record CasesFile(IReadOnlyList<RelevanceCase> Cases);

    private sealed record FixtureFile(IReadOnlyList<FixtureTable> Tables);

    private sealed record FixtureTable(
        string Id,
        string DisplayName,
        string Description,
        string? LongDescription,
        FixtureTexts? En,
        IReadOnlyList<string>? Tags,
        IReadOnlyList<FixtureColumn> Columns);

    private sealed record FixtureTexts(string? DisplayName, string? Description);

    private sealed record FixtureColumn(
        string Name,
        string Type,
        string Description,
        string? En);
}

/// <summary>
/// Supplies the relevance cases of one <see cref="RelevanceNeed"/> in every <see cref="RelevanceMode"/> as (case id, mode).
/// The <c>Skip</c> text of the attribute is the switch: a feature PR removes it for the need it builds, in the same PR.
/// Cases that need embeddings are skipped in both modes because no multilingual model runs in the tests.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class RelevanceCasesAttribute(string need) : DataAttribute
{
    public string Need { get; } = need;

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        RelevanceFixture.Cases
            .Where(c => c.Needs == Need)
            .SelectMany(c => RelevanceMode.All.Select(mode => new object[] { c.Id, mode }));
}
