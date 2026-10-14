namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Text.RegularExpressions;

/// <summary>One corpus statement. <see cref="Expected"/> is the intent recorded by the extractor (accept or reject); it is metadata, not a verdict.</summary>
public sealed record CorpusStatement(string Id, string Class, string Expected, string Source, string Sql)
{
    public bool IsDml => Class != "select";
}

/// <summary>Reads the seed corpus (<c>Corpus/dql.sql</c>, <c>Corpus/dml.sql</c>) written by <c>tools/SqlCorpusExtractor</c>.</summary>
public static partial class CorpusLoader
{
    [GeneratedRegex(@"^-- @id (?<id>[0-9a-f]{12}) class=(?<class>\w+) expected=(?<expected>\w+) source=(?<source>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    public static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Corpus");

    public static IReadOnlyList<CorpusStatement> LoadAll() => [.. Load("dql.sql"), .. Load("dml.sql")];

    public static IReadOnlyList<CorpusStatement> Load(string file) => Parse(File.ReadAllText(Path.Combine(CorpusDirectory, file)));

    public static IReadOnlyList<CorpusStatement> Parse(string text)
    {
        var result = new List<CorpusStatement>();
        string? id = null, cls = null, expected = null, source = null;
        var body = new List<string>();

        void Flush()
        {
            if (id is not null)
            {
                result.Add(new CorpusStatement(id, cls!, expected!, source!, string.Join('\n', body).Trim()));
            }

            body.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var m = Header().Match(line);
            if (m.Success)
            {
                Flush();
                id = m.Groups["id"].Value;
                cls = m.Groups["class"].Value;
                expected = m.Groups["expected"].Value;
                source = m.Groups["source"].Value;
            }
            else
            {
                body.Add(line);
            }
        }

        Flush();
        return result;
    }
}
