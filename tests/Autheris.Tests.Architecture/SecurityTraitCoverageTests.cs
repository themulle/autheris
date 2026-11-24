namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SecurityTraitCoverageTests
{
    private const string ExemptionsRelativePath = "tests/Autheris.Tests.Architecture/Baselines/security-trait-exemptions.txt";
    private const string SecurityTestsBaselineRelativePath = "tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt";
    private const string RemovedSecurityTestsRelativePath = "tests/Autheris.Tests.Architecture/Baselines/removed-security-tests.txt";
    private const int InitialExemptionCeiling = 0;

    private static readonly string[] ListedRegressionTestClasses =
    [
        "AccessPlanningTenantBindingTests",
        "IdentityDeprovisioningServiceTests",
        "AsyncJobWorkerConcurrencyTests",
        "CdcSubscriptionPrincipalTests",
        "SidFallbackTests",
        "EntraTokenPolicyAccessTokenShapeTests",
        "ReadOnlyTokenTests",
        "JwtSocketTokenValidatorTests",
        "AsyncQueryJobManagerTests",
        "GraphQlBatchingSecurityTests",
        "GatewayStartupValidatorTests",
        "CsvExportTests",
        "CdcWebhookDeliveryServiceTests",
        "HitLStepUpApprovalServiceTests",
        "AsyncQueryJobScratchQuotaTests",
        "AnalyticsEndpointsSecurityTests",
        "DbtSemanticManifestIngestionServiceTests"
    ];

    [Fact]
    public void Listed_regression_tests_carry_security_trait()
    {
        var (allTests, securityTests) = TestInventoryScanner.ScanRepository();
        var allSources = RepositoryScan.EnumerateFiles("tests", "*.cs")
            .Concat(RepositoryScan.EnumerateFiles("modules", "*.cs"))
            .Select(RepositoryScan.ReadText)
            .ToList();

        var missingClasses = new List<string>();
        var untaggedMethods = new List<string>();

        foreach (var className in ListedRegressionTestClasses)
        {
            var marker = $".{className}.";
            var classMethods = allTests
                .Where(fqn => fqn.Contains(marker, StringComparison.Ordinal))
                .ToList();

            if (classMethods.Count == 0)
            {
                var declRegex = new Regex($@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.CultureInvariant);
                var matchingSource = allSources.FirstOrDefault(declRegex.IsMatch);
                if (matchingSource is null)
                {
                    missingClasses.Add(className);
                    continue;
                }

                var fileAll = new HashSet<string>(StringComparer.Ordinal);
                var fileSec = new HashSet<string>(StringComparer.Ordinal);
                TestInventoryScanner.ExtractFromSource(matchingSource, fileAll, fileSec);
                if (fileAll.Count == 0)
                {
                    missingClasses.Add(className);
                    continue;
                }

                foreach (var methodFqn in fileAll)
                {
                    if (!fileSec.Contains(methodFqn))
                    {
                        untaggedMethods.Add($"{className}:{methodFqn}");
                    }
                }

                continue;
            }

            foreach (var methodFqn in classMethods)
            {
                if (!securityTests.Contains(methodFqn))
                {
                    untaggedMethods.Add(methodFqn);
                }
            }
        }

        missingClasses.ShouldBeEmpty(
            $"SEC-OI-504-1: Listed security regression test classes were not found in the repository: {string.Join(", ", missingClasses)}");
        untaggedMethods.ShouldBeEmpty(
            $"SEC-OI-504-1: Listed security regression test classes have methods without [Trait(\"Category\", \"Security\")]: {string.Join(", ", untaggedMethods.Take(20))}");
    }

    [Fact]
    public void Security_folder_and_named_classes_carry_security_trait()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var exemptions = LoadExemptions(root).Keys.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var file in RepositoryScan.EnumerateFiles("tests", "*.cs").Concat(RepositoryScan.EnumerateFiles("modules", "*.cs")))
        {
            var rel = RepositoryScan.ToRelativePath(file);
            var fileName = Path.GetFileName(rel);
            bool inSecurityFolder = rel.Contains("/Security/", StringComparison.Ordinal);
            bool isSecurityNamedFile = fileName.Contains("Security", StringComparison.Ordinal) &&
                                       fileName.EndsWith("Tests.cs", StringComparison.Ordinal);

            var source = RepositoryScan.ReadText(file);
            var nsMatch = TestInventoryScanner.NamespaceRegex().Match(source);
            var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "Global";
            bool inSecurityNamespace = ns.Contains(".Security", StringComparison.Ordinal);

            if (!inSecurityFolder && !inSecurityNamespace && !isSecurityNamedFile)
            {
                continue;
            }

            var fileAll = new HashSet<string>(StringComparer.Ordinal);
            var fileSec = new HashSet<string>(StringComparer.Ordinal);
            TestInventoryScanner.ExtractFromSource(source, fileAll, fileSec);

            foreach (var testFqn in fileAll)
            {
                int lastDot = testFqn.LastIndexOf('.');
                var typeFullName = lastDot > 0 ? testFqn[..lastDot] : testFqn;
                if (exemptions.Contains(typeFullName))
                {
                    continue;
                }

                if (!fileSec.Contains(testFqn))
                {
                    violations.Add($"{testFqn} ({rel})");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"SEC-OI-504-1/2: Every test in a Security/ folder, *.Security* namespace, or *Security*Tests.cs file must carry [Trait(\"Category\", \"Security\")]. Missing on: {string.Join(", ", violations.Take(25))}");
    }

    [Fact]
    public void Method_level_traits_are_recognised()
    {
        const string sampleSource = """
        namespace Autheris.Tests.Unit.Sample;
        using Xunit;

        public sealed class MixedTraitSampleTests
        {
            [Fact]
            [Trait("Category", "Security")]
            public void Security_tagged_method() { }

            [Fact]
            public void Plain_functional_method() { }
        }

        [Trait("Category", "Security")]
        public sealed class ClassTaggedSampleTests
        {
            [Fact]
            public void Inherits_class_security_trait() { }
        }
        """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(sampleSource, allTests, securityTests);

        allTests.ShouldContain("Autheris.Tests.Unit.Sample.MixedTraitSampleTests.Security_tagged_method");
        allTests.ShouldContain("Autheris.Tests.Unit.Sample.MixedTraitSampleTests.Plain_functional_method");
        allTests.ShouldContain("Autheris.Tests.Unit.Sample.ClassTaggedSampleTests.Inherits_class_security_trait");

        securityTests.ShouldContain("Autheris.Tests.Unit.Sample.MixedTraitSampleTests.Security_tagged_method");
        securityTests.ShouldNotContain("Autheris.Tests.Unit.Sample.MixedTraitSampleTests.Plain_functional_method");
        securityTests.ShouldContain("Autheris.Tests.Unit.Sample.ClassTaggedSampleTests.Inherits_class_security_trait");
    }

    [Fact]
    public void Baseline_may_only_shrink()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var exemptions = LoadExemptions(root);

        exemptions.Count.ShouldBeLessThanOrEqualTo(
            InitialExemptionCeiling,
            $"SEC-OI-504-1: {ExemptionsRelativePath} is a shrink-only baseline (ceiling {InitialExemptionCeiling}, actual {exemptions.Count}). Tag new test classes with [Trait(\"Category\", \"Security\")] instead of adding exemptions.");
    }

    [Fact]
    public void Security_test_name_set_does_not_lose_entries()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselinePath = Path.Combine(root, SecurityTestsBaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselinePath).ShouldBeTrue($"Missing baseline file: {SecurityTestsBaselineRelativePath}");

        var (_, currentSecurityTests) = TestInventoryScanner.ScanRepository();
        var coverageMap = TestInventoryScanner.LoadCoverageMap(root);
        var removedSecurityTests = LoadRemovedSecurityTests(root);

        var baseline = File.ReadAllLines(baselinePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        var unmappedMissing = baseline
            .Where(testId => !currentSecurityTests.Contains(testId) &&
                             !coverageMap.ContainsKey(testId) &&
                             !removedSecurityTests.ContainsKey(testId))
            .ToList();

        unmappedMissing.ShouldBeEmpty(
            $"SEC-OI-504-3: Security tests from {SecurityTestsBaselineRelativePath} were removed or lost their [Trait(\"Category\", \"Security\")] without an entry in {RemovedSecurityTestsRelativePath}: {string.Join(", ", unmappedMissing.Take(20))}");
    }

    private static Dictionary<string, string> LoadExemptions(string root)
    {
        var path = Path.Combine(root, ExemptionsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Missing baseline file: {ExemptionsRelativePath}");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t', 2);
            parts.Length.ShouldBe(2, $"Malformed line in {ExemptionsRelativePath} (expected '<TypeFullName>\\t<Reason>'): '{raw}'");
            var typeName = parts[0].Trim();
            var reason = parts[1].Trim();
            typeName.ShouldNotBeEmpty();
            reason.ShouldNotBeEmpty($"Exemption for '{typeName}' in {ExemptionsRelativePath} must include a non-empty reason.");
            result[typeName] = reason;
        }

        return result;
    }

    private static Dictionary<string, (string Ref, string Reason)> LoadRemovedSecurityTests(string root)
    {
        var path = Path.Combine(root, RemovedSecurityTestsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Missing baseline file: {RemovedSecurityTestsRelativePath}");

        var result = new Dictionary<string, (string Ref, string Reason)>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t', 3);
            parts.Length.ShouldBe(3, $"Malformed line in {RemovedSecurityTestsRelativePath} (expected '<TestFqn>\\t<PrOrIssueRef>\\t<Reason>'): '{raw}'");
            var testFqn = parts[0].Trim();
            var issueRef = parts[1].Trim();
            var reason = parts[2].Trim();
            testFqn.ShouldNotBeEmpty();
            issueRef.ShouldNotBeEmpty();
            reason.ShouldNotBeEmpty();
            result[testFqn] = (issueRef, reason);
        }

        return result;
    }
}

[Trait("Category", "Security")]
public sealed class SecurityTestInventoryTests
{
    private const string InventoryRelativePath = "tests/Autheris.Tests.Architecture/Baselines/security-test-inventory.txt";

    [Fact]
    public void Inventory_file_matches_discovered_tests()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var inventoryPath = Path.Combine(root, InventoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(inventoryPath).ShouldBeTrue($"Missing generated security test inventory file: {InventoryRelativePath}");

        var (_, discoveredSet) = TestInventoryScanner.ScanRepository();
        var discovered = discoveredSet.OrderBy(fqn => fqn, StringComparer.Ordinal).ToList();

        if (string.Equals(Environment.GetEnvironmentVariable("AUTHERIS_UPDATE_SECURITY_INVENTORY"), "1", StringComparison.Ordinal))
        {
            File.WriteAllLines(inventoryPath, discovered);
            var basePath = Path.Combine(root, "tests", "Autheris.Tests.Architecture", "Baselines", "security-tests-base.txt");
            var map = TestInventoryScanner.LoadCoverageMap(root);
            var mappedHistorical = File.ReadAllLines(basePath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && map.ContainsKey(l));
            var updatedBase = discoveredSet.Union(mappedHistorical, StringComparer.Ordinal)
                .OrderBy(fqn => fqn, StringComparer.Ordinal)
                .ToList();
            File.WriteAllLines(basePath, updatedBase);
        }

        var recorded = File.ReadAllLines(inventoryPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        var missingFromFile = discovered.Except(recorded, StringComparer.Ordinal).ToList();
        var staleInFile = recorded.Except(discovered, StringComparer.Ordinal).ToList();

        missingFromFile.ShouldBeEmpty(
            $"Q-SEC9-1: {InventoryRelativePath} is missing {missingFromFile.Count} discovered Category=Security tests (first 20): {string.Join(", ", missingFromFile.Take(20))}");
        staleInFile.ShouldBeEmpty(
            $"Q-SEC9-1: {InventoryRelativePath} contains {staleInFile.Count} stale entries not found by TestInventoryScanner (first 20): {string.Join(", ", staleInFile.Take(20))}");
        recorded.ShouldBe(discovered, $"Q-SEC9-1: {InventoryRelativePath} must be sorted in ordinal order.");
    }
}
