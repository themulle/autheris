using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// GOV-12: no consent row may be materialised without passing <c>IConsentMaterializationGuard</c>. The consent write seam
/// (<c>AttributeValidatingConsentRepository</c>, which delegates to the guard) covers <c>CreateConsentAsync</c> and
/// <c>ReplaceConsentsAsync</c>; the approval paths bypass that decorator, so the governance repositories call the guard themselves
/// before they take a connection or write scope.
/// </summary>
[Trait("Category", "Security")]
public sealed class ConsentMaterializationGuardArchitectureTests
{
    private static readonly string[] RepositoryFiles =
    [
        "GovernanceRepository.Consent.cs",
        "GovernanceRepository.ConsentWorkflow.cs",
        "GovernanceRepository.ConsentGrants.cs"
    ];

    // Shared insert helper used by CreateConsentAsync / ReplaceConsentsCoreAsync / atomic approvals; the seam (decorator or the atomic method) guards it.
    private const string SharedInsert = "InsertConsentCoreAsync";
    private const string GrantWriterInsert = "InsertAndGrantAsync";

    private static readonly Regex MemberStart = new(@"^ {4}(?: {4})?(?:public|private|internal|protected)(?!.*\bclass\b)[^=;(]*\s(\w+)\(", RegexOptions.Compiled);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir!.FullName;
    }

    private static Dictionary<string, string> Methods(string path)
    {
        var methods = new Dictionary<string, System.Text.StringBuilder>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in File.ReadLines(path))
        {
            var match = MemberStart.Match(line);
            if (match.Success)
            {
                current = match.Groups[1].Value;
                if (!methods.ContainsKey(current))
                {
                    methods[current] = new System.Text.StringBuilder();
                }
            }

            if (current is not null)
            {
                methods[current].AppendLine(line);
            }
        }

        return methods.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public void EveryConsentInsertSiteCallsMaterializationGuard()
    {
        var root = FindRepositoryRoot();
        var persistenceDir = Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence");
        var violations = new List<string>();

        // 1. All raw INSERT INTO CONSENTS SQL is centralized in GovernanceRepository.GrantRecords.cs and executed only by InsertConsentRowAsync.
        var nonMigrationFiles = Directory.EnumerateFiles(persistenceDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/Migrations/", StringComparison.Ordinal))
            .ToList();
        var rawInsertFiles = nonMigrationFiles
            .Where(f => File.ReadAllText(f).Contains("INSERT INTO CONSENTS", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();
        rawInsertFiles.ShouldBe(["GovernanceRepository.GrantRecords.cs"]);

        var grantCoreMethods = Methods(Path.Combine(persistenceDir, "GovernanceRepository.GrantRecords.cs"));
        var rowInsertCallers = grantCoreMethods
            .Where(kv => kv.Value.Contains("InsertConsentSql", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        rowInsertCallers.ShouldBe(["InsertConsentRowAsync"]);

        // 2. GrantWriter.InsertAndGrantAsync is the single Application-layer caller of InsertConsentRowAsync.
        var grantWriterMethods = Methods(Path.Combine(root, "src", "Autheris.Application", "Governance", "Grants", "Records", "GrantWriter.cs"));
        var writerCallers = grantWriterMethods
            .Where(kv => kv.Value.Contains("InsertConsentRowAsync", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        writerCallers.ShouldBe([GrantWriterInsert]);

        // 3. In the governance consent repositories, every method reaching InsertAndGrantAsync or InsertConsentCoreAsync
        // (outside the AttributeValidatingConsentRepository-decorated CreateConsentAsync / ReplaceConsentsCoreAsync)
        // must call EnsureMayMaterializeAsync.
        var decoratedEntries = new HashSet<string>(StringComparer.Ordinal) { "CreateConsentAsync", "ReplaceConsentsCoreAsync" };
        bool foundSharedInsert = false;

        foreach (var repoFile in RepositoryFiles)
        {
            var file = Path.Combine(persistenceDir, repoFile);
            var methods = Methods(file);
            if (methods.ContainsKey(SharedInsert))
            {
                foundSharedInsert = true;
            }

            foreach (var entry in methods.Where(kv =>
                         kv.Key != SharedInsert &&
                         !decoratedEntries.Contains(kv.Key) &&
                         (kv.Value.Contains(SharedInsert + "(", StringComparison.Ordinal) ||
                          kv.Value.Contains(GrantWriterInsert + "(", StringComparison.Ordinal))))
            {
                if (!entry.Value.Contains("EnsureMayMaterializeAsync(", StringComparison.Ordinal))
                {
                    violations.Add($"{repoFile}: {entry.Key} reaches consent insert without calling the materialisation guard");
                }
            }
        }

        foundSharedInsert.ShouldBeTrue($"The shared consent insert helper '{SharedInsert}' was not found.");
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }
}
