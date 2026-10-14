using System.Text.Json;
using System.Text.Json.Serialization;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>One Trino input of the shared conformance corpus (plan 9.3 G2). A <c>@name</c> statement refers to an in-memory AST fixture.</summary>
internal sealed record ConformanceInput(string Id, string Group, string Sql);

/// <summary>A bound parameter as recorded in a fixture: marker, logical type, origin and the value in its invariant text form.</summary>
internal sealed record ExpectedParameter(string Marker, string Type, string Origin, string ValueType, string? Value);

/// <summary>
/// One golden entry: the Trino input, the outcome for one dialect and the expected SQL text and parameter list
/// (sqlglot-style <c>validate_all</c>). <see cref="Outcome"/> is <c>ok</c> or <c>rejected</c>; a rejection records the exception type.
/// </summary>
internal sealed record ConformanceFixture(
    string Id,
    string Group,
    string Sql,
    string Outcome,
    string? Expected,
    IReadOnlyList<ExpectedParameter> Parameters,
    string? ExceptionType,
    string? SemanticNote);

internal sealed record ConformanceFile(string Dialect, IReadOnlyList<ConformanceFixture> Cases);

/// <summary>The six production dialects of the G2 gate (the Snowflake threshold is dropped from the X1 gate, plan 28.3).</summary>
internal static class ConformanceDialects
{
    public static readonly IReadOnlyList<(string Name, TargetSqlDialect Dialect)> All = new[]
    {
        ("PostgreSql", TargetSqlDialect.PostgreSql),
        ("SqlServer", TargetSqlDialect.SqlServer),
        ("Sqlite", TargetSqlDialect.Sqlite),
        ("Oracle", TargetSqlDialect.Oracle),
        ("DuckDb", TargetSqlDialect.DuckDb),
        ("Databricks", TargetSqlDialect.Databricks)
    };

    public static IEnumerable<object[]> Names() => All.Select(d => new object[] { d.Name });

    public static TargetSqlDialect Parse(string name) => All.Single(d => d.Name == name).Dialect;

    /// <summary>Minimum number of cases per production dialect (plan 9.3 G2).</summary>
    public const int MinCasesPerDialect = 300;
}

internal static class ConformanceStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "Conformance", "Fixtures");

    /// <summary>The checked-in source folder, found by walking up from the test binary to the project directory.</summary>
    public static string SourceDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TrinoSqlEngine.Tests.csproj"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Test project directory not found."), "Conformance", "Fixtures");
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConformanceFile> Cache = new();

    public static ConformanceFile Load(string dialectName) => Cache.GetOrAdd(dialectName, name =>
    {
        var path = Path.Combine(OutputDirectory, name + ".json");
        return JsonSerializer.Deserialize<ConformanceFile>(File.ReadAllText(path), Json)
               ?? throw new InvalidOperationException($"Fixture file {path} is empty.");
    });

    public static IReadOnlyDictionary<string, Dictionary<string, string>> LoadNotes(string directory)
    {
        var path = Path.Combine(directory, "semantic-notes.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path), Json)
               ?? new Dictionary<string, Dictionary<string, string>>();
    }

    public static IReadOnlyList<ConformanceInput> LoadCorpus(string directory)
    {
        var inputs = new List<ConformanceInput>();
        foreach (var line in File.ReadAllLines(Path.Combine(directory, "corpus.txt")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var first = line.IndexOf(';');
            var second = line.IndexOf(';', first + 1);
            inputs.Add(new ConformanceInput(line[(first + 1)..second], line[..first], line[(second + 1)..]));
        }
        return inputs;
    }

    public static ExpectedParameter Describe(BoundParameter p) => new(
        p.Marker, p.Type.ToString(), p.Origin.ToString(), p.Value?.GetType().Name ?? "null",
        p.Value switch
        {
            null => null,
            byte[] b => Convert.ToHexString(b),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var v => v.ToString()
        });
}
