using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// WP-X0v, plan M-5 (SEC-ADG-13 e): security-tagged tests (<c>Category=Security</c>) are never retried. A flaky security test fails
/// the gate; it can only be quarantined with a security CODEOWNERS approval and a linked issue. A retry that turns a red security
/// test green would hide exactly the intermittent failure that a race in an isolation control produces.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "SqlGate")]
public partial class SecurityTestPolicyTests
{
    // test-runner retry switches and retry attributes of common libraries
    [GeneratedRegex(@"--retry-failed-tests|RetryFailedTests|retry-failed-tests|nick-fields/retry|Retry(Fact|Theory)\b|\[\s*Retry\s*[\(\]]|RerunFailed|rerun-failed|--blame-hang-retry", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryPattern();

    [Fact]
    public void SecurityTests_AreNeverRetried()
    {
        var files = new List<string>();
        files.AddRange(RepositoryFiles.Search("tests/**/*.cs"));
        files.AddRange(RepositoryFiles.Search("tests/**/*.csproj"));
        files.AddRange(RepositoryFiles.Search("tests/**/*.runsettings"));
        files.AddRange(RepositoryFiles.Search("tests/**/xunit.runner.json"));
        files.AddRange(RepositoryFiles.Search(".github/**/*.yml"));
        files.AddRange(RepositoryFiles.Search(".github/**/*.yaml"));
        files.AddRange(RepositoryFiles.Search("Directory.Build.props"));
        files.AddRange(RepositoryFiles.Search("Directory.Build.targets"));

        var offenders = files
            .Where(f => !f.EndsWith(nameof(SecurityTestPolicyTests) + ".cs", StringComparison.Ordinal))
            .Where(f => RetryPattern().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryFiles.Root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A retry mechanism is configured; security tests must never be retried (plan M-5). Quarantine needs security CODEOWNERS approval and a linked issue: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void EveryTestOfTheSecurityGate_IsTaggedCategorySecurity()
    {
        var untagged = typeof(SecurityTestPolicyTests).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(SecurityTestPolicyTests).Namespace && t.IsPublic)
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.GetCustomAttributes(typeof(FactAttribute), true).Length > 0))
            .Where(t => !SecurityRegressionTraceabilityTests.HasSecurityCategory(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(untagged.Count == 0, "Gate classes without Category=Security (they would escape the no-retry policy): " + string.Join(", ", untagged));
    }
}
