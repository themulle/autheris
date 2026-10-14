using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// WP-X0v, G7 (plan section 9.3, SEC-ADG-13): 100% ID traceability. The IDs are extracted from the test sources, so a new
/// <c>SEC-</c>, <c>SQ-</c>, <c>SQL-</c> or <c>SR15-</c> ID cannot be added without a catalog row, and a row cannot name a test that
/// does not exist or does not run on the AST path.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "SqlGate")]
public partial class SecurityRegressionTraceabilityTests
{
    // not preceded by a letter, digit or hyphen (so R-SQL-3, F-SQL-02, SEC-ADG-08 and ST-1 do not match), numeric suffix only
    [GeneratedRegex(@"(?<![A-Za-z0-9\-])(?:SEC|SQ|SQL|SR15|RR)-\d+(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyIdPattern();

    private static readonly string[] CatalogFiles = { "SecurityRegressionCatalog.cs", "SecurityRegressionTraceabilityTests.cs" };

    private static IEnumerable<string> TestSources() =>
        RepositoryFiles.Search("tests/**/*.cs").Where(f => !CatalogFiles.Contains(Path.GetFileName(f)));

    internal static bool HasSecurityCategory(MemberInfo member) =>
        member.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute) &&
            a.ConstructorArguments.Count == 2 &&
            a.ConstructorArguments[0].Value as string == "Category" &&
            a.ConstructorArguments[1].Value as string == "Security");

    private static string TestsPath(string relative) => Path.Combine(RepositoryFiles.Root, "tests", relative);

    /// <summary>Every legacy SEC/SQ/SQL/SR15 ID of the test sources has a catalog row, and a Compiler row has an AST-path test.</summary>
    [Fact]
    public void EveryLegacyId_HasSameOrStricterAstTest()
    {
        var inSources = TestSources()
            .SelectMany(f => LegacyIdPattern().Matches(File.ReadAllText(f)).Select(m => m.Value))
            .ToHashSet(StringComparer.Ordinal);
        var catalog = SecurityRegressionCatalog.Legacy.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var uncatalogued = inSources.Where(id => !catalog.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.True(uncatalogued.Count == 0, "Legacy IDs without a SecurityRegressionCatalog row: " + string.Join(", ", uncatalogued));

        var withoutAst = SecurityRegressionCatalog.Legacy
            .Where(e => e.Kind == LegacyIdKind.Compiler && e.AstTests.Length == 0 && (e.ElsewhereAstTests?.Length ?? 0) == 0)
            .Select(e => e.Id).ToList();
        Assert.True(withoutAst.Count == 0, "Compiler IDs without an AST-path test: " + string.Join(", ", withoutAst));
    }

    /// <summary>Every AST test named in the catalog exists in this assembly as a Fact or Theory of the AST regression suite.</summary>
    [Fact]
    public void CatalogAstTests_ExistAndAreFactsOrTheories_OfTheSecuritySuite()
    {
        var problems = new List<string>();
        foreach (var entry in SecurityRegressionCatalog.Legacy)
        {
            foreach (string name in entry.AstTests)
            {
                int dot = name.LastIndexOf('.');
                var type = typeof(SecurityRegressionCatalog).Assembly.GetTypes().SingleOrDefault(t => t.Name == name[..dot]);
                var method = type?.GetMethod(name[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance);
                if (method is null || !method.GetCustomAttributes().Any(a => a is FactAttribute))
                {
                    problems.Add($"{entry.Id}: {name} is not a Fact or Theory");
                    continue;
                }

                if (!HasSecurityCategory(type!) && !HasSecurityCategory(method))
                {
                    // an existing suite may be named as evidence, but then it must be part of the security category
                    problems.Add($"{entry.Id}: {name} is not tagged Category=Security (it would be retried or skipped like an ordinary test)");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The owner files exist and carry the ID, and the AST tests of other projects exist (typo and rename check).</summary>
    [Fact]
    public void CatalogOwnerFiles_ExistAndCarryTheId()
    {
        var problems = new List<string>();
        foreach (var entry in SecurityRegressionCatalog.Legacy)
        {
            if (entry.Owners.Length == 0) problems.Add($"{entry.Id}: no owner file");
            foreach (string owner in entry.Owners)
            {
                string path = TestsPath(owner);
                if (!File.Exists(path)) problems.Add($"{entry.Id}: {owner} does not exist");
                else if (!File.ReadAllText(path).Contains(entry.Id, StringComparison.Ordinal)) problems.Add($"{entry.Id}: {owner} does not mention the ID");
            }

            foreach (string elsewhere in entry.ElsewhereAstTests ?? Array.Empty<string>())
            {
                var parts = elsewhere.Split('#');
                string path = TestsPath(parts[0]);
                if (!File.Exists(path) || !File.ReadAllText(path).Contains(parts[1], StringComparison.Ordinal)) problems.Add($"{entry.Id}: {elsewhere} does not exist");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The catalog has no duplicate rows and every Compiler row explains itself.</summary>
    [Fact]
    public void Catalog_HasNoDuplicates_AndEveryRowHasASummary()
    {
        Assert.Equal(SecurityRegressionCatalog.Legacy.Count, SecurityRegressionCatalog.Legacy.Select(e => e.Id).Distinct().Count());
        Assert.All(SecurityRegressionCatalog.Legacy, e => Assert.False(string.IsNullOrWhiteSpace(e.Summary), e.Id));
    }

    /// <summary>All 29 Phase 3 IDs have a row with an owner; where the control is on this branch the evidence exists.</summary>
    [Fact]
    public void Phase3Ids_AreCatalogued_WithOwnersAndExistingEvidence()
    {
        var ids = SecurityRegressionCatalog.Phase3.Select(e => e.Id).ToList();
        Assert.Equal(Enumerable.Range(1, 29).Select(i => $"SEC-ADG-{i:00}"), ids);

        var problems = new List<string>();
        foreach (var entry in SecurityRegressionCatalog.Phase3)
        {
            if (entry.WorkPackages.Length == 0) problems.Add($"{entry.Id}: no owning work package");
            foreach (string evidence in entry.Evidence)
            {
                string[] parts = evidence.Split("::");
                string path = TestsPath(parts[0]);
                if (!File.Exists(path) || !File.ReadAllText(path).Contains(parts[1], StringComparison.Ordinal)) problems.Add($"{entry.Id}: evidence {evidence} not found");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// With every X0 package merged, only the IDs whose control is the WP-X1 cutover itself may lack evidence. A new row without
    /// evidence must be a conscious decision: add it here together with the owning package.
    /// </summary>
    [Fact]
    public void Phase3IdsWithoutEvidence_AreOnlyThoseOwnedByTheX1Cutover()
    {
        var without = SecurityRegressionCatalog.Phase3.Where(e => e.Evidence.Length == 0).Select(e => e.Id).ToArray();
        // SEC-ADG-28: the removal of the WebSql:SqlRewriterEngine key and its fail-fast startup check are part of the cutover (WP-X1).
        string[] expected = { "SEC-ADG-28" };
        Assert.Equal(expected, without);
    }
}
