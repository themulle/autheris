using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-SC-16 / SEC-SC-20: every code path that writes, activates or extends a grant consults <c>IGrantPolicy</c>
/// (time-bound individual grants). New writers must call the policy or be added to the allowlist with a reason.
/// </summary>
public class IndividualGrantPolicyArchitectureTests
{
    private static readonly Regex GrantWriter = new(
        @"\.(CreateConsentAsync|ExtendConsentExpiryAsync|ReplaceConsentsAsync|UpsertProfileAsync|ActivateConsentAsync|ActivateConsentForAutoApproveAsync|CreateConsentRequestAsync|AddTupleAsync|AddTuplesAsync)\(",
        RegexOptions.Compiled);

    /// <summary>Files that write grants without calling the policy, each with the reason it is safe.</summary>
    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Autheris.Application/Identity/Attributes/AttributeValidatingConsentRepository.cs"] =
            "Decorator of IConsentRepository (REQ-GOV-129): validates caller attribute row filters and forwards unchanged; it neither creates nor extends a validity window.",
        ["Autheris.Extensions/OpenMetadata/OpenMetadataSyncService.cs"] =
            "System reconcile of consents derived from OpenMetadata rules; short validity refreshed on each run and covered by the read-time clamp.",
        ["Autheris.Extensions/Itsm/ItsmWebhookHandler.cs"] =
            "Activates a request whose duration was validated by IGrantPolicy at submission; no new expiry is introduced.",
        ["Autheris.Api/Extensions/DependencyInjection/RebacSeedHostedService.cs"] =
            "Operator-configured seed tuples (Rebac.SeedTuples / development defaults) written at startup; not a runtime grant path.",
        ["Autheris.Extensions/DataCatalog/DataCatalogSyncService.cs"] =
            "Writes only structural 'parent' tuples (schema/domain hierarchy); no user grant.",
        ["Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs"] =
            "Writes only structural 'parent' tuples (schema/domain hierarchy); no user grant.",
    };

    [Fact]
    public void SEC_SC_20_EveryGrantWriter_ConsultsTheGrantPolicy_OrIsAllowlisted()
    {
        var src = Path.Combine(FindRepositoryRoot(), "src");
        var offenders = new List<string>();
        var seenAllowlisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateSources(src))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.Contains("/Persistence/", StringComparison.Ordinal) || rel.EndsWith("IGovernanceRepository.cs", StringComparison.Ordinal))
            {
                continue; // repository implementations and contracts
            }

            var text = File.ReadAllText(file);
            var matches = GrantWriter.Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            if (Allowlist.ContainsKey(rel))
            {
                seenAllowlisted.Add(rel);
                continue;
            }

            // Per call site: the enclosing method (or, for an expression-bodied member, the statement) must itself consult the policy.
            foreach (Match m in matches)
            {
                var scope = EnclosingMethodText(text, m.Index);
                if (!ConsultsPolicy.IsMatch(scope))
                {
                    var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                    offenders.Add($"{rel}:{line} {m.Value.Trim('.', '(')}");
                }
            }
        }

        offenders.ShouldBeEmpty("These files write grants but never consult IGrantPolicy (WP-SC-16). Call the policy or allowlist with a reason.");
        seenAllowlisted.Count.ShouldBe(Allowlist.Count, "A stale allowlist entry no longer writes grants; remove it.");
    }

    [Fact]
    public void SEC_SC_20_Scanner_IsPerCallSite_NotPerFileText()
    {
        const string code = """
            class Svc(IGrantPolicy policy)
            {
                Task Guarded() { policy.Evaluate(new GrantRequest()); return repo.CreateConsentAsync(c); }
                Task Unguarded() { return repo.CreateConsentAsync(c); }
                Task Nested() { foreach (var x in xs) { if (x) { repo.AddTupleAsync(x); } } }
            }
            """;
        var matches = GrantWriter.Matches(code);

        matches.Count.ShouldBe(3);
        ConsultsPolicy.IsMatch(EnclosingMethodText(code, matches[0].Index)).ShouldBeTrue();
        ConsultsPolicy.IsMatch(EnclosingMethodText(code, matches[1].Index)).ShouldBeFalse("GrantPolicy elsewhere in the file must not satisfy another method");
        ConsultsPolicy.IsMatch(EnclosingMethodText(code, matches[2].Index)).ShouldBeFalse();
    }

    private static readonly Regex ConsultsPolicy = new(@"GrantPolicy|GrantRequest\b|EnsureGrantsAllowed", RegexOptions.Compiled);

    private static readonly Regex TypeHeader = new(@"\b(class|struct|record|interface|enum|namespace)\b", RegexOptions.Compiled);

    /// <summary>Text of the outermost non-type block enclosing <paramref name="index"/> (the method body); the whole file when there is none.</summary>
    internal static string EnclosingMethodText(string text, int index)
    {
        var open = new Stack<int>();
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '{')
            {
                open.Push(i);
            }
            else if (text[i] == '}' && open.Count > 0)
            {
                open.Pop();
            }
        }

        // Outermost first.
        foreach (var start in open.Reverse())
        {
            var headerStart = Math.Max(text.LastIndexOfAny([';', '}', '{'], Math.Max(start - 1, 0)) + 1, 0);
            var header = text.Substring(headerStart, start - headerStart);
            if (TypeHeader.IsMatch(header))
            {
                continue;
            }

            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                {
                    return header + text[start..(i + 1)];
                }
            }
        }

        // Not inside a block: expression-bodied member; use the statement.
        var from = text.LastIndexOfAny([';', '}'], index) + 1;
        var to = text.IndexOf(';', index);
        return text[from..(to < 0 ? text.Length : to + 1)];
    }

    private static IEnumerable<string> EnumerateSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Autheris.sln) not found.");
    }
}
