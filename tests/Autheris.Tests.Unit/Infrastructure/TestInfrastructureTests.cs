using System.Text.RegularExpressions;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Infrastructure;

/// <summary>
/// SEC-SIMPL-50 and SEC-SIMPL-93: test infrastructure security invariants.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class TestInfrastructureTests
{
    [GeneratedRegex(@"\bdapi[0-9a-f]{32}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DatabricksPatLiteralRegex();

    [GeneratedRegex(@"\$\{\{\s*secrets\.(DATABRICKS|SPARK|ORACLE)[A-Z0-9_]*\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex DialectSecretReferenceRegex();

    [Fact]
    [Trait("Category", "Security")]
    public void Missing_runtime_fails_in_ci_and_skips_locally()
    {
        // 1. In CI (CI=true or CI=1 or GITHUB_ACTIONS=true), missing container runtime never skips (so the test runs and fails).
        var ciFact = new RequiresContainerRuntimeFactAttribute(
            key => key == "CI" ? "true" : null,
            () => false);
        ciFact.Skip.ShouldBeNull("CI=true must not skip when container runtime is missing.");

        var ciOneFact = new RequiresContainerRuntimeFactAttribute(
            key => key == "CI" ? "1" : null,
            () => false);
        ciOneFact.Skip.ShouldBeNull("CI=1 must not skip when container runtime is missing.");

        var ghaTheory = new RequiresContainerRuntimeTheoryAttribute(
            key => key == "GITHUB_ACTIONS" ? "true" : null,
            () => false);
        ghaTheory.Skip.ShouldBeNull("GITHUB_ACTIONS=true must not skip when container runtime is missing.");

        // 2. Locally (CI and GITHUB_ACTIONS unset/false), missing runtime skips with explicit local-only marker.
        var localMissingFact = new RequiresContainerRuntimeFactAttribute(
            _ => null,
            () => false);
        localMissingFact.Skip.ShouldBe(RequiresContainerRuntimeFactAttribute.LocalSkipReason);

        var localMissingTheory = new RequiresContainerRuntimeTheoryAttribute(
            key => key == "CI" ? "false" : null,
            () => false);
        localMissingTheory.Skip.ShouldBe(RequiresContainerRuntimeFactAttribute.LocalSkipReason);

        // 3. When runtime is available, neither local nor CI skips.
        var localAvailableFact = new RequiresContainerRuntimeFactAttribute(
            _ => null,
            () => true);
        localAvailableFact.Skip.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_databricks_spark_or_oracle_credentials_in_repo_and_secrets_only_in_nightly_workflow()
    {
        var repoRoot = LocateRepoRoot();
        var workflowsDir = Path.Combine(repoRoot, ".github", "workflows");
        Directory.Exists(workflowsDir).ShouldBeTrue(".github/workflows directory must exist.");

        var workflowFiles = Directory.GetFiles(workflowsDir, "*.yml", SearchOption.TopDirectoryOnly);
        workflowFiles.ShouldNotBeEmpty();

        var nonNightlySecretViolations = new List<string>();
        var patLiteralViolations = new List<string>();

        foreach (var wf in workflowFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(wf);
            var text = File.ReadAllText(wf);

            if (DatabricksPatLiteralRegex().IsMatch(text))
            {
                patLiteralViolations.Add($".github/workflows/{fileName}");
            }

            bool isNightly = fileName.Contains("nightly", StringComparison.OrdinalIgnoreCase);
            if (!isNightly && DialectSecretReferenceRegex().IsMatch(text))
            {
                nonNightlySecretViolations.Add(fileName);
            }
        }

        nonNightlySecretViolations.ShouldBeEmpty(
            "Databricks/Spark/Oracle CI secrets may only be referenced in nightly workflows (SEC-SIMPL-93).");

        // Scan test files for real Databricks PAT tokens (dapi + 32 hex chars).
        var testsDir = Path.Combine(repoRoot, "tests");
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (DatabricksPatLiteralRegex().IsMatch(text))
            {
                patLiteralViolations.Add(Path.GetRelativePath(repoRoot, file));
            }
        }

        patLiteralViolations.ShouldBeEmpty("No Databricks PAT literal may exist in the repository (SEC-SIMPL-93).");

        // Negative self-check on regexes
        DatabricksPatLiteralRegex().IsMatch(string.Concat("dapi", new string('a', 32))).ShouldBeTrue();
        DialectSecretReferenceRegex().IsMatch("${{ secrets.DATABRICKS_HOST }}").ShouldBeTrue();
        DialectSecretReferenceRegex().IsMatch("${{ secrets.ORACLE_PASSWORD }}").ShouldBeTrue();
        DialectSecretReferenceRegex().IsMatch("${{ secrets.SPARK_TOKEN }}").ShouldBeTrue();
    }

    private static string LocateRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Autheris.sln from " + AppContext.BaseDirectory);
    }
}
