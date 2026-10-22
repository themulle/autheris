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
    private static readonly string[] Providers = ["PostgreSql", "SqlServer", "Sqlite"];

    // Shared insert helper used by CreateConsentAsync / the atomic approval; the seam (decorator or the atomic method) guards it.
    private const string SharedInsert = "InsertConsentCoreAsync";

    private static readonly Regex MemberStart = new(@"^    (?:public|private|internal|protected)[^=;(]*\s(\w+)\(", RegexOptions.Compiled);

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
        var violations = new List<string>();

        foreach (var provider in Providers)
        {
            var file = Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence", $"{provider}GovernanceRepository.Consent.cs");
            var methods = Methods(file);

            var insertSites = methods.Where(kv => kv.Value.Contains("INSERT INTO CONSENTS", StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
            insertSites.ShouldContain(SharedInsert, $"{provider}: the shared consent insert helper was not found (rename the architecture rule together with it).");

            foreach (var site in insertSites.Where(s => s != SharedInsert))
            {
                if (!methods[site].Contains("EnsureMayMaterializeAsync(", StringComparison.Ordinal))
                {
                    violations.Add($"{provider}: {site} inserts into CONSENTS without calling the materialisation guard");
                }
            }

            // Entry points that reach the shared insert while bypassing the IConsentRepository decorator.
            foreach (var entry in methods.Where(kv => kv.Value.Contains(SharedInsert + "(", StringComparison.Ordinal) && kv.Key != SharedInsert && kv.Key != "CreateConsentAsync"))
            {
                if (!entry.Value.Contains("EnsureMayMaterializeAsync(", StringComparison.Ordinal))
                {
                    violations.Add($"{provider}: {entry.Key} reaches {SharedInsert} without calling the materialisation guard");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }
}
