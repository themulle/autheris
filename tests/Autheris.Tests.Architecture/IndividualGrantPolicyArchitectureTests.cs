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
        @"\.(CreateConsentAsync|ExtendConsentExpiryAsync|ReplaceConsentsAsync|UpsertProfileAsync|ActivateConsentAsync|ActivateConsentForAutoApproveAsync|CreateConsentRequestAsync)\(",
        RegexOptions.Compiled);

    /// <summary>Files that write grants without calling the policy, each with the reason it is safe.</summary>
    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Autheris.Extensions/OpenMetadata/OpenMetadataSyncService.cs"] =
            "System reconcile of consents derived from OpenMetadata rules; short validity refreshed on each run and covered by the read-time clamp.",
        ["Autheris.Extensions/Itsm/ItsmWebhookHandler.cs"] =
            "Activates a request whose duration was validated by IGrantPolicy at submission; no new expiry is introduced.",
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
            if (!GrantWriter.IsMatch(text))
            {
                continue;
            }

            if (Allowlist.ContainsKey(rel))
            {
                seenAllowlisted.Add(rel);
                continue;
            }

            if (!text.Contains("GrantPolicy", StringComparison.Ordinal))
            {
                offenders.Add(rel);
            }
        }

        offenders.ShouldBeEmpty("These files write grants but never consult IGrantPolicy (WP-SC-16). Call the policy or allowlist with a reason.");
        seenAllowlisted.Count.ShouldBe(Allowlist.Count, "A stale allowlist entry no longer writes grants; remove it.");
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
