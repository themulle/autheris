using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #94 (D-9, OD-6, SEC-94-10): the declared table definition is the only key source. Production code does not query a source
/// database catalog for primary keys, unique constraints or indexes, so the source login needs nothing beyond <c>SELECT</c>.
/// <para>
/// Two files are sanctioned exceptions, each with a reason. The governance migration and shape code under
/// <c>Persistence/Migrations</c> reads the governance database (not a source), so it is out of scope.
/// </para>
/// </summary>
[Trait("Category", "Security")]
public sealed class ArchitectureNoSourceKeyCatalogQueriesTests
{
    /// <summary>Case-insensitive patterns of key and index catalog reads. Each one has a positive sample in <see cref="Samples"/>.</summary>
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("sys.indexes", P(@"\bsys\.indexes\b")),
        ("sys.index_columns", P(@"\bsys\.index_columns\b")),
        ("sys.key_constraints", P(@"\bsys\.key_constraints\b")),
        ("pg_index", P(@"\bpg_index(es)?\b")),
        ("pg_constraint", P(@"\bpg_constraint\b")),
        ("information_schema.table_constraints", P(@"information_schema\.table_constraints\b")),
        ("information_schema.key_column_usage", P(@"information_schema\.key_column_usage\b")),
        ("information_schema.constraint_column_usage", P(@"information_schema\.constraint_column_usage\b")),
        ("ALL_CONSTRAINTS", P(@"\b(ALL|USER|DBA)_CONSTRAINTS\b")),
        ("ALL_INDEXES", P(@"\b(ALL|USER|DBA)_INDEXES\b")),
        ("ALL_IND_COLUMNS", P(@"\b(ALL|USER|DBA)_IND_COLUMNS\b")),
        ("ALL_CONS_COLUMNS", P(@"\b(ALL|USER|DBA)_CONS_COLUMNS\b")),
        ("duckdb_constraints", P(@"\bduckdb_constraints\b")),
        ("duckdb_indexes", P(@"\bduckdb_indexes\b")),
        ("pragma_index_list/info/xinfo", P(@"\bpragma_index_(list|info|xinfo)\b")),
        ("PRAGMA index_list/info/xinfo", P(@"\bPRAGMA\s+index_(list|info|xinfo)\b")),
        ("PRAGMA table_info pk", P(@"\b(pragma_table_x?info\s*\([^)]*\)|PRAGMA\s+table_x?info\s*\([^)]*\))[^\n]*\bpk\b"))
    ];

    private static Regex P(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Exact-path allowlist (relative to <c>src</c>, forward slashes) with the justification of each entry.</summary>
    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.Ordinal)
    {
        ["Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs"] =
            "Proves PHYSICAL uniqueness for row-scope authorization (fails closed); a declared, unverified index must not replace it (SEC-94-10).",
        ["Autheris.Infrastructure/Persistence/Catalog/PostgreSqlKeyIndexMetadataReader.cs"] =
            "Issue #403: reads only the column DIRECTION of the declared keys, never a key source; the declared definition stays the only key (I403-SC-04, SC-06).",
        ["Autheris.Infrastructure/Persistence/Catalog/SqlServerKeyIndexMetadataReader.cs"] =
            "Issue #403: reads only the column DIRECTION of the declared keys, never a key source; the declared definition stays the only key (I403-SC-04, SC-06)."
    };

    private const string MigrationsDirectory = "Autheris.Infrastructure/Persistence/Migrations/";

    private static readonly string Src = Path.Combine(RepositoryRoot(), "src");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static string Relative(string file) => Path.GetRelativePath(Src, file).Replace('\\', '/');

    private static IEnumerable<string> AllSourceFiles() =>
        Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Relative(f);
                return !rel.Contains("/obj/", StringComparison.Ordinal) && !rel.Contains("/bin/", StringComparison.Ordinal);
            });

    /// <summary>Files in scope: everything under src except the governance migration and shape code.</summary>
    private static IEnumerable<string> ScannedFiles() =>
        AllSourceFiles().Where(f => !Relative(f).StartsWith(MigrationsDirectory, StringComparison.Ordinal));

    private static IEnumerable<string> Hits(string text) =>
        Patterns.Where(p => p.Pattern.IsMatch(text)).Select(p => p.Name);

    [Fact]
    public void NoSourceKeyCatalogQueries_OutsideTheAllowlist()
    {
        var violations = ScannedFiles()
            .Where(f => !Allowlist.ContainsKey(Relative(f)))
            .Select(f => (File: Relative(f), Hits: Hits(File.ReadAllText(f)).ToList()))
            .Where(x => x.Hits.Count > 0)
            .Select(x => $"{x.File}: {string.Join(", ", x.Hits)}")
            .ToList();

        violations.ShouldBeEmpty(
            "The declared key/index definition is the only key source (D-9). Do not query a source catalog for keys: " + string.Join("; ", violations));
    }

    [Fact]
    public void ExactAllowlist()
    {
        Allowlist.Keys.Order(StringComparer.Ordinal).ShouldBe(
        [
            "Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs",
            "Autheris.Infrastructure/Persistence/Catalog/PostgreSqlKeyIndexMetadataReader.cs",
            "Autheris.Infrastructure/Persistence/Catalog/SqlServerKeyIndexMetadataReader.cs"
        ]);
        foreach (var (path, reason) in Allowlist)
        {
            reason.ShouldNotBeNullOrWhiteSpace();
            path.StartsWith(MigrationsDirectory, StringComparison.Ordinal).ShouldBeFalse("a migration path never needs an allowlist entry");
            var file = Path.Combine(Src, path);
            File.Exists(file).ShouldBeTrue($"allowlisted file is missing: {path}");
            // an entry that no longer queries a key catalog is stale and must be removed
            Hits(File.ReadAllText(file)).ShouldNotBeEmpty($"stale allowlist entry: {path}");
        }
    }

    [Fact]
    public void MigrationsOutOfScope()
    {
        var scanned = ScannedFiles().Select(Relative).ToList();

        scanned.ShouldNotContain(f => f.StartsWith(MigrationsDirectory, StringComparison.Ordinal));
        AllSourceFiles().Select(Relative).ShouldContain(f => f.StartsWith(MigrationsDirectory, StringComparison.Ordinal), "positive control: the migrations exist");
        // positive controls: the scope really covers the code that must stay free of key catalog reads
        scanned.ShouldContain(f => f.StartsWith("Autheris.Infrastructure/Persistence/Catalog/", StringComparison.Ordinal));
        scanned.ShouldContain(f => f.StartsWith("Autheris.Application/Data/", StringComparison.Ordinal));
        scanned.ShouldContain(f => f.StartsWith("Autheris.Extensions/OData/", StringComparison.Ordinal));
        // the change tracking producer reads keys from the declared definition only (REQ-CDC-99 D-A3): it must be in the scanned set
        scanned.ShouldContain(f => f.StartsWith("Autheris.Extensions/Cdc/", StringComparison.Ordinal));
        scanned.ShouldContain("Autheris.Extensions/Cdc/MssqlChangeTrackingPoller.cs");
        scanned.ShouldContain("Autheris.Extensions/Cdc/CdcTableActivator.cs");
        scanned.ShouldContain("Autheris.Extensions/Cdc/CdcSourceQueries.cs");
    }

    private static readonly (string Pattern, string Sample)[] Samples =
    [
        ("sys.indexes", "SELECT * FROM SYS.INDEXES i"),
        ("sys.index_columns", "JOIN sys.index_columns ic ON 1=1"),
        ("sys.key_constraints", "FROM sys.key_constraints"),
        ("pg_index", "FROM pg_index i"),
        ("pg_constraint", "FROM Pg_Constraint c"),
        ("information_schema.table_constraints", "FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS"),
        ("information_schema.key_column_usage", "FROM information_schema.key_column_usage"),
        ("information_schema.constraint_column_usage", "FROM information_schema.constraint_column_usage"),
        ("ALL_CONSTRAINTS", "FROM user_constraints"),
        ("ALL_INDEXES", "FROM dba_indexes"),
        ("ALL_IND_COLUMNS", "FROM all_ind_columns"),
        ("ALL_CONS_COLUMNS", "FROM ALL_CONS_COLUMNS"),
        ("duckdb_constraints", "FROM duckdb_constraints()"),
        ("duckdb_indexes", "FROM duckdb_indexes()"),
        ("pragma_index_list/info/xinfo", "FROM pragma_index_xinfo('x')"),
        ("PRAGMA index_list/info/xinfo", "PRAGMA index_list('t')"),
        ("PRAGMA table_info pk", "SELECT name FROM pragma_table_info(@t) WHERE pk > 0")
    ];

    [Fact]
    public void EachPatternDetected()
    {
        Samples.Select(s => s.Pattern).Order(StringComparer.Ordinal).ShouldBe(Patterns.Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var (name, sample) in Samples)
        {
            Hits(sample).ShouldContain(name, $"pattern '{name}' did not detect: {sample}");
        }
    }

    [Theory]
    [InlineData("SELECT name, type FROM pragma_table_info(@t, @s) ORDER BY cid")]
    [InlineData("CREATE TABLE TABLE_INDEX_COLUMNS (id INTEGER)")]
    [InlineData("INSERT INTO TABLE_INDEXES (name) VALUES (@n)")]
    [InlineData("SELECT column_name FROM information_schema.columns")]
    public void GovernanceTablesAndColumnReads_AreNotKeyCatalogReads(string sql) => Hits(sql).ShouldBeEmpty();
}
