using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #94 (SEC-94, SEC-94-10, WP-SIMPL-44): Autheris reads data from data sources and never their metadata. Production code does not send a statement that reads a
/// schema or system catalog (information_schema, sys.*, metadata functions, pg_*, Oracle dictionary views, SHOW/DESCRIBE) to a
/// source, so the source login needs <c>SELECT</c> on the governed tables and nothing else. Column types, keys and indexes come
/// from the declared catalog. The runtime guard (<c>SourceMetadataGuard</c>) is the second line of defence.
/// <para>
/// Allowlist changes need a CODEOWNERS security review (this file is a security-owned path).
/// </para>
/// </summary>
[Trait("Category", "Security")]
public sealed class ArchitectureNoSourceMetadataReadsTests
{
    /// <summary>Case-insensitive patterns of metadata reads. Each one has a positive sample in <see cref="Samples"/>.</summary>
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("information_schema", P(@"\binformation_schema\b")),
        ("sys.*", P(@"\bsys\.(?!sp_set_session_context\b)[a-z_]+")),
        ("metadata functions", P(@"\b(OBJECT_ID|OBJECT_NAME|OBJECT_SCHEMA_NAME|OBJECTPROPERTY(EX)?|COLUMNPROPERTY|INDEXPROPERTY|COL_LENGTH|OBJECT_DEFINITION|HAS_PERMS_BY_NAME)\s*\(")),
        ("sp_ catalog procedures", P(@"\bsp_(help|describe_|columns|tables|pkeys|fkeys|statistics|spaceused|stored_procedures|sproc_columns|special_columns)")),
        ("pg_catalog", P(@"\bpg_catalog\b")),
        ("pg_ relations", P(@"\bpg_(class|attribute|index|constraint|namespace|proc|type|description|stat[a-z_]*|statistic|settings|tables|views|indexes|matviews|roles|authid|database)\b")),
        ("pg_get_ functions", P(@"\bpg_get_[a-z_]+\s*\(")),
        ("::reg casts", P(@"::reg(class|proc|type)\b")),
        ("Oracle dictionary", P(@"\b(ALL|USER|DBA|CDB)_(TABLES|TAB_COLUMNS|TAB_COLS|VIEWS|OBJECTS|CONSTRAINTS|CONS_COLUMNS|INDEXES|IND_COLUMNS|ARGUMENTS|PROCEDURES|SOURCE|SYNONYMS|TAB_STATISTICS|TAB_COL_STATISTICS|TAB_COMMENTS|COL_COMMENTS|DEPENDENCIES|POLICIES|SEGMENTS|TYPES|MVIEWS)\b")),
        ("Oracle V$ views", P(@"\bG?V\$[A-Z_]+")),
        ("DBMS_METADATA", P(@"\bDBMS_METADATA\b")),
        ("SHOW/DESCRIBE statements", P(@"""\s*(SHOW|DESCRIBE|DESC|ANALYZE)\s+")),
        ("duckdb catalog functions", P(@"\bduckdb_(columns|constraints|indexes)\b")),
        ("pragma_ table functions", P(@"\bpragma_(table_x?info|index_)")),
        ("CommandBehavior KeyInfo/SchemaOnly", P(@"CommandBehavior\.(KeyInfo|SchemaOnly)")),
        ("GetSchema", P(@"\.GetSchema\(")),
        ("DeriveParameters", P(@"\bDeriveParameters\b"))
    ];

    private static Regex P(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Permanent exact-path allowlist (relative to <c>src</c>, forward slashes), each with its reason.</summary>
    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.Ordinal)
    {
        ["Autheris.Extensions/Cdc/CdcSourceQueries.cs"] =
            "The only metadata function: CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t)) resolves a declared, granted object and has no data-only alternative.",
        ["Autheris.Application/Sql/SourceMetadataGuard.cs"] = "The runtime guard holds the rule vocabulary; it rejects, never sends.",
        ["Autheris.Infrastructure/Persistence/MetadataGuardedConnection.cs"] = "Rejects CommandBehavior.KeyInfo/SchemaOnly; never sets them.",
        ["Autheris.Application/Governance/Validation/RowFilterAstValidator.cs"] = "denylist/emulation literal, never sent to a source",
        ["Autheris.Application/Procedures/Services/ProcedureDefinitionLoader.cs"] = "denylist/emulation literal, never sent to a source",
        ["TrinoSqlEngine/SqlFunctionPolicy.cs"] = "denylist/emulation literal, never sent to a source",
        ["Autheris.Infrastructure/Persistence/SessionPinning.cs"] = "denylist/emulation literal, never sent to a source: pg_catalog is a search_path entry, not a read",
        ["Autheris.Application/Sql/Services/TrinoMetadataQueryHandler.cs"] = "denylist/emulation literal, never sent to a source: answers from the declared catalog",
        ["Autheris.Application/Catalog/Services/CatalogSourceNameValidator.cs"] = "denylist literal, never sent to a source: validates against reserved catalog names",
        ["Autheris.Application/VirtualFilters/EffectiveFilterInspector.cs"] = "audit target label literal, never sent to a source"
    };

    private const string Removed = "removed in PR 2-4";

    /// <summary>Temporary entries: the readers and validators that the declared-catalog work packages still delete.</summary>
    private static readonly Dictionary<string, string> TemporaryAllowlist = new(StringComparer.Ordinal)
    {
        ["Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs"] = Removed,
        ["Autheris.Application/Procedures/Services/StoredProcedureCatalogValidator.cs"] = Removed,
        ["Autheris.Infrastructure/Persistence/Catalog/PostgreSqlKeyIndexMetadataReader.cs"] = Removed,
        ["Autheris.Infrastructure/Persistence/Catalog/SqlServerKeyIndexMetadataReader.cs"] = Removed
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

    /// <summary>The governance database belongs to Autheris: its migrations, repositories, stores and shape checks are not data sources.</summary>
    private static bool IsGovernanceDatabaseCode(string rel)
    {
        if (rel.StartsWith(MigrationsDirectory, StringComparison.Ordinal))
        {
            return true;
        }

        var name = Path.GetFileName(rel);
        if (name is "GovernanceSchemaValidator.cs" or "DbCatalogVersionProvider.cs")
        {
            return true;
        }

        return rel.StartsWith("Autheris.Infrastructure/Persistence/", StringComparison.Ordinal)
            && rel.Count(c => c == '/') == 2
            && name != "SqlConnectionFactory.cs"
            && (name.Contains("GovernanceRepository", StringComparison.Ordinal)
                || name.EndsWith("SqlCore.cs", StringComparison.Ordinal)
                || name.EndsWith("Store.cs", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ScannedFiles() => AllSourceFiles().Where(f => !IsGovernanceDatabaseCode(Relative(f)));

    /// <summary>Comment-stripped code: lines that start with <c>//</c> or <c>///</c> are dropped.</summary>
    private static string Code(string file) =>
        string.Join('\n', File.ReadLines(file).Where(l => !l.AsSpan().TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static IEnumerable<string> Hits(string text) =>
        Patterns.Where(p => p.Pattern.IsMatch(text)).Select(p => p.Name);

    [Fact]
    public void No_source_metadata_read()
    {
        var violations = ScannedFiles()
            .Where(f => !Allowlist.ContainsKey(Relative(f)) && !TemporaryAllowlist.ContainsKey(Relative(f)))
            .Select(f => (File: Relative(f), Hits: Hits(Code(f)).ToList()))
            .Where(x => x.Hits.Count > 0)
            .Select(x => $"{x.File}: {string.Join(", ", x.Hits)}")
            .ToList();

        violations.ShouldBeEmpty(
            "Autheris reads data, never source metadata. Column types, keys and indexes come from the declared catalog: " + string.Join("; ", violations));
    }

    [Fact]
    public void NoCommandBehaviorKeyInfoOrDeriveParameters()
    {
        var driverMetadata = Patterns.Where(p => p.Name is "CommandBehavior KeyInfo/SchemaOnly" or "DeriveParameters" or "GetSchema").ToList();
        driverMetadata.Count.ShouldBe(3);
        var violations = ScannedFiles()
            .Where(f => !Allowlist.ContainsKey(Relative(f)))
            .Where(f => driverMetadata.Any(p => p.Pattern.IsMatch(Code(f))))
            .Select(Relative)
            .ToList();

        violations.ShouldBeEmpty("Driver-side schema discovery (KeyInfo, SchemaOnly, GetSchema, DeriveParameters) is a metadata read: " + string.Join("; ", violations));
    }

    [Fact]
    public void Allowlist_EntriesExistAndStillMatch()
    {
        Allowlist.Keys.Intersect(TemporaryAllowlist.Keys).ShouldBeEmpty();
        foreach (var (path, reason) in Allowlist.Concat(TemporaryAllowlist))
        {
            reason.ShouldNotBeNullOrWhiteSpace();
            IsGovernanceDatabaseCode(path).ShouldBeFalse("governance database code is excluded by rule and never needs an allowlist entry");
            var file = Path.Combine(Src, path);
            File.Exists(file).ShouldBeTrue($"allowlisted file is missing: {path}");
            // an entry that no longer reads metadata is stale and must be removed
            Hits(Code(file)).ShouldNotBeEmpty($"stale allowlist entry: {path}");
        }
    }

    [Fact]
    public void MigrationsOutOfScope()
    {
        var scanned = ScannedFiles().Select(Relative).ToList();

        scanned.ShouldNotContain(f => f.StartsWith(MigrationsDirectory, StringComparison.Ordinal));
        AllSourceFiles().Select(Relative).ShouldContain(f => f.StartsWith(MigrationsDirectory, StringComparison.Ordinal), "positive control: the migrations exist");
        // positive controls: the scope really covers the code that must stay free of metadata reads
        scanned.ShouldContain(f => f.StartsWith("Autheris.Infrastructure/Persistence/Catalog/", StringComparison.Ordinal));
        scanned.ShouldContain(f => f.StartsWith("Autheris.Application/DataSources/", StringComparison.Ordinal));
        scanned.ShouldContain(f => f.StartsWith("Autheris.Application/OData/", StringComparison.Ordinal));
        scanned.ShouldContain(f => f.StartsWith("Autheris.Extensions/Cdc/", StringComparison.Ordinal));
        scanned.ShouldContain("Autheris.Extensions/Cdc/MssqlChangeTrackingPoller.cs");
        scanned.ShouldContain("Autheris.Extensions/Cdc/CdcTableActivator.cs");
        scanned.ShouldContain("Autheris.Extensions/Cdc/CdcSourceQueries.cs");
        scanned.ShouldContain("Autheris.Infrastructure/Persistence/SqlConnectionFactory.cs");
    }

    [Fact]
    public void KeyIndexReaders_AreReferencedOnlyByTheGovernedCatalogProviderAndComposition()
    {
        const string Persistence = "Autheris.Infrastructure/Persistence/Catalog/";
        const string Composition = "Autheris.Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs";
        var concrete = new[] { "SqlServerKeyIndexMetadataReader", "PostgreSqlKeyIndexMetadataReader" };
        var port = new[]
        {
            "Autheris.Application/Sql/Catalog/IKeyIndexMetadataReader.cs", Persistence + "SqlServerKeyIndexMetadataReader.cs",
            Persistence + "PostgreSqlKeyIndexMetadataReader.cs", Persistence + "GovernedCatalogProvider.cs",
            Composition
        };

        var portUsers = AllSourceFiles().Where(f => File.ReadAllText(f).Contains("IKeyIndexMetadataReader", StringComparison.Ordinal)).Select(Relative).Order(StringComparer.Ordinal);
        portUsers.ShouldBe(port.Order(StringComparer.Ordinal));

        var concreteUsers = AllSourceFiles()
            .Where(f => { var text = File.ReadAllText(f); return concrete.Any(c => text.Contains(c, StringComparison.Ordinal)); })
            .Select(Relative).Order(StringComparer.Ordinal);
        concreteUsers.ShouldBe(new[] { Composition, Persistence + "PostgreSqlKeyIndexMetadataReader.cs", Persistence + "SqlServerKeyIndexMetadataReader.cs" }.Order(StringComparer.Ordinal));
    }

    private static readonly (string Pattern, string Sample)[] Samples =
    [
        ("information_schema", "SELECT * FROM INFORMATION_SCHEMA.COLUMNS"),
        ("sys.*", "SELECT * FROM sys.objects"),
        ("metadata functions", "SELECT OBJECT_ID (@t)"),
        ("sp_ catalog procedures", "EXEC sp_help @t"),
        ("pg_catalog", "SET search_path = pg_catalog"),
        ("pg_ relations", "FROM Pg_Stat_Activity"),
        ("pg_get_ functions", "SELECT pg_get_viewdef(1)"),
        ("::reg casts", "SELECT 't'::regclass"),
        ("Oracle dictionary", "FROM all_tab_columns"),
        ("Oracle V$ views", "FROM gv$session"),
        ("DBMS_METADATA", "SELECT DBMS_METADATA.GET_DDL('TABLE','T')"),
        ("SHOW/DESCRIBE statements", "var s = \"SHOW TABLES\";"),
        ("duckdb catalog functions", "FROM duckdb_columns()"),
        ("pragma_ table functions", "FROM pragma_table_info('t')"),
        ("CommandBehavior KeyInfo/SchemaOnly", "reader = cmd.ExecuteReader(CommandBehavior.KeyInfo)"),
        ("GetSchema", "connection.GetSchema(\"Tables\")"),
        ("DeriveParameters", "SqlCommandBuilder.DeriveParameters(cmd)")
    ];

    [Fact]
    public void EveryPattern_HasPositiveSample()
    {
        Samples.Select(s => s.Pattern).Order(StringComparer.Ordinal).ShouldBe(Patterns.Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var (name, sample) in Samples)
        {
            Hits(sample).ShouldContain(name, $"pattern '{name}' did not detect: {sample}");
        }
    }

    [Theory]
    [InlineData("CREATE TABLE TABLE_INDEX_COLUMNS (id INTEGER)")]
    [InlineData("INSERT INTO TABLE_INDEXES (name) VALUES (@n)")]
    [InlineData("EXEC sys.sp_set_session_context @key = N'k', @value = @v")]
    [InlineData("SELECT * FROM USER_PROFILES WHERE orders_stats > 1")]
    public void GovernanceTables_AreNotMetadataReads(string sql) => Hits(sql).ShouldBeEmpty();
}
