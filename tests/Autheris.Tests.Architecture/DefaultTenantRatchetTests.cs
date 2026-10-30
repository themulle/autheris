using System.Text.RegularExpressions;
using Shouldly;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

/// <summary>
/// DT-SC-12 (WP-DT-6): the default tenant is deployment configuration (<c>Gateway:Tenancy:DefaultTenantId</c>) and nothing else. The legacy
/// single-tenant bridge (<c>Mode=Single</c> shim, the <c>legacy-single-tenant</c> tenant) is gone, and no code falls back to a literal
/// tenant named <c>default</c> (DT-A-9). The scan covers every text file under src/, deploy/, benchmarks/ and the root docker-compose and
/// Dockerfile files (appsettings and Helm included). Explicit exclusions, each with its reason:
/// the three frozen governance baseline schemas (column DEFAULT 'legacy-single-tenant'; dropped by a migration step in WP-DT-2b) and
/// <c>LegacySwitchGuard.cs</c>, which must name the removed keys to reject them.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class DefaultTenantRatchetTests(ITestOutputHelper output)
{
    private static readonly string[] TextExtensions =
        [".cs", ".json", ".yaml", ".yml", ".tpl", ".sh", ".ps1", ".example", ".env", ".toml", ".props", ".txt"];

    private static readonly string[] SkippedFiles = ["packages.lock.json"];

    /// <summary>Frozen governance baselines: the column default 'legacy-single-tenant' is the only allowed remaining literal.</summary>
    private static readonly string[] FrozenBaselines =
    [
        "src/Autheris.Infrastructure/Persistence/Migrations/PostgreSqlBaselineSchema.cs",
        "src/Autheris.Infrastructure/Persistence/Migrations/SqlServerBaselineSchema.cs",
        "src/Autheris.Infrastructure/Persistence/Migrations/SqliteBaselineSchema.cs"
    ];

    /// <summary>The removed-key guard names the removed keys by design.</summary>
    private const string RemovedKeyGuard = "src/Autheris.Api/Configuration/LegacySwitchGuard.cs";

    /// <summary>Known literal-"default" tenant fallbacks (DT-A-9 residue outside the WP-DT-6 scope). Shrink only: path|exact count.</summary>
    private static readonly IReadOnlyDictionary<string, int> LiteralDefaultAllowance = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Autheris.Application/FinOps/Services/FocusCostAccountingService.cs"] = 3,
        ["src/Autheris.Application/Security/Rebac/Services/InMemoryRebacStore.cs"] = 1,
        ["src/Autheris.Application/Security/Rebac/Services/ZanzibarRebacEvaluator.cs"] = 4,
        ["src/Autheris.Infrastructure/Rebac/RedisRebacStore.cs"] = 1
    };

    private static readonly (string Name, Regex Pattern)[] Forbidden =
    [
        ("LegacySingleTenant", LegacyTenantSymbol()),
        ("TenancyMode", TenancyModeSymbol()),
        ("TryParseMode", TryParseModeSymbol()),
        ("Tenancy EffectiveMode", TenancyEffectiveMode()),
        ("Tenancy Mode shim", TenancyModeAccess()),
        ("TenantId(\"default\")", DefaultTenantIdLiteral()),
        ("Tenancy:Mode", TenancyModeKey()),
        ("Tenancy__Mode", TenancyModeEnvKey()),
        ("legacy-single-tenant", LegacyTenantLiteral())
    ];

    [GeneratedRegex(@"\bLegacySingleTenant\b")]
    private static partial Regex LegacyTenantSymbol();

    [GeneratedRegex(@"\bTenancyMode\b")]
    private static partial Regex TenancyModeSymbol();

    [GeneratedRegex(@"\bTryParseMode\b")]
    private static partial Regex TryParseModeSymbol();

    [GeneratedRegex(@"(?i)tenancy\w*\??\.EffectiveMode\b")]
    private static partial Regex TenancyEffectiveMode();

    [GeneratedRegex(@"(?i)tenancy\w*\??\.Mode\b")]
    private static partial Regex TenancyModeAccess();

    [GeneratedRegex(@"TenantId\(\s*""default""\s*\)")]
    private static partial Regex DefaultTenantIdLiteral();

    [GeneratedRegex(@"Tenancy:Mode")]
    private static partial Regex TenancyModeKey();

    [GeneratedRegex(@"Tenancy__Mode")]
    private static partial Regex TenancyModeEnvKey();

    [GeneratedRegex(@"legacy-single-tenant")]
    private static partial Regex LegacyTenantLiteral();

    // A tenant variable or call on the same line that is assigned or falls back to the literal "default": 'tenantId = "default"',
    // '? "default" : tenant', '?? "default"'. A "default" domain, namespace or realm name (an argument after a comma, a realm id) is not matched.
    [GeneratedRegex(@"(?i)tenant")]
    private static partial Regex MentionsTenant();

    [GeneratedRegex(@"(?:(?<![=!<>])=|\?\?|\?|:)\s*""default""(?![\w-])(?!\s*,)")]
    private static partial Regex LiteralDefaultFallback();

    private static IEnumerable<(string Path, string Text)> ScannedFiles()
    {
        var paths = new List<string>();
        foreach (var dir in new[] { "src", "deploy", "benchmarks" })
        {
            paths.AddRange(RepositoryScan.EnumerateFiles(dir, "*"));
        }

        var root = RepositoryScan.RepoRoot;
        paths.AddRange(Directory.EnumerateFiles(root, "docker-compose*").Select(f => RepositoryScan.Normalize(Path.GetRelativePath(root, f))));
        paths.AddRange(Directory.EnumerateFiles(root, "Dockerfile*").Select(f => RepositoryScan.Normalize(Path.GetRelativePath(root, f))));

        foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path);
            var isText = TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                || name.StartsWith("docker-compose", StringComparison.Ordinal)
                || name.StartsWith("Dockerfile", StringComparison.Ordinal);
            if (!isText || SkippedFiles.Contains(name, StringComparer.Ordinal) || path.Contains("/results/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (path, RepositoryScan.ReadText(path));
        }
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n');

    [Fact]
    public void DT_SC_12_No_Legacy_Single_Tenant_Bridge_Remains()
    {
        var files = ScannedFiles().ToList();
        files.Count.ShouldBeGreaterThan(500, "the scan must really see src, deploy and benchmarks");
        files.ShouldContain(f => f.Path.EndsWith("appsettings.Development.json", StringComparison.Ordinal));
        files.ShouldContain(f => f.Path.StartsWith("deploy/helm/", StringComparison.Ordinal));
        files.ShouldContain(f => f.Path.StartsWith("benchmarks/", StringComparison.Ordinal));
        files.ShouldContain(f => f.Path == "docker-compose.yml");

        var violations = new List<string>();
        foreach (var (path, text) in files)
        {
            var isFrozen = FrozenBaselines.Contains(path, StringComparer.Ordinal);
            var isGuard = string.Equals(path, RemovedKeyGuard, StringComparison.Ordinal);
            foreach (var (name, pattern) in Forbidden)
            {
                if (isFrozen && name == "legacy-single-tenant")
                {
                    continue;
                }

                if (isGuard && name is "Tenancy:Mode" or "Tenancy__Mode")
                {
                    continue;
                }

                if (pattern.IsMatch(text))
                {
                    violations.Add($"{path}: {name}");
                }
            }
        }

        violations.ForEach(output.WriteLine);
        violations.ShouldBeEmpty("DT-SC-12: the legacy single-tenant bridge must stay removed");
    }

    [Fact]
    public void DT_SC_12_The_Frozen_Baselines_Are_The_Only_Place_Of_The_Legacy_Tenant_Literal()
    {
        var holders = ScannedFiles()
            .Where(f => LegacyTenantLiteral().IsMatch(f.Text))
            .Select(f => f.Path)
            .ToList();

        holders.Except(FrozenBaselines).ShouldBeEmpty();
        // Non-vacuity: the exclusion list is real; drop an entry once WP-DT-2b removes the column default.
        holders.ShouldBe(FrozenBaselines.Order(StringComparer.Ordinal).ToList(), ignoreOrder: true);
    }

    [Fact]
    public void DT_A_9_No_Literal_Default_Tenant_Fallback_Beyond_The_Shrink_Only_Allowance()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, text) in ScannedFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var hits = Lines(text)
                .Where(l => !RepositoryScan.IsCommentLine(l.TrimStart()) && MentionsTenant().IsMatch(l) && LiteralDefaultFallback().IsMatch(l))
                .Count();
            if (hits > 0)
            {
                counts[path] = hits;
            }
        }

        counts.ToList().ForEach(c => output.WriteLine($"{c.Key}|{c.Value}"));

        var problems = new List<string>();
        foreach (var (path, count) in counts)
        {
            if (!LiteralDefaultAllowance.TryGetValue(path, out var allowed) || count > allowed)
            {
                problems.Add($"{path}: {count} literal \"default\" tenant fallback(s), allowed {(LiteralDefaultAllowance.TryGetValue(path, out var a) ? a : 0)}");
            }
        }

        foreach (var (path, allowed) in LiteralDefaultAllowance)
        {
            if (!counts.TryGetValue(path, out var count) || count < allowed)
            {
                problems.Add($"{path}: allowance {allowed} is stale (found {(counts.TryGetValue(path, out var c) ? c : 0)}); lower it");
            }
        }

        problems.ShouldBeEmpty("DT-A-9: use the resolved tenant or fail closed with TENANT_UNRESOLVED instead of a literal \"default\" tenant");
    }

    [Theory]
    [InlineData("tenantId = \"default\";", true)]
    [InlineData("var tenant = string.IsNullOrWhiteSpace(t) ? \"default\" : t.Trim();", true)]
    [InlineData("var tenant = t ?? \"default\";", true)]
    [InlineData("tenantId = ok ? t : \"default\";", true)]
    [InlineData("new TableIdentifier(tenant.Value, \"default\", modelName)", false)]
    [InlineData("realm: \"default\", tenant: t", false)]
    [InlineData("TenantResolutionOutcome.DefaultAssigned => \"default\",", false)]
    [InlineData("var domain = \"default\";", false)]
    public void DT_A_9_Detector_Matches_The_Fallback_Pattern_Only(string line, bool expected) =>
        (MentionsTenant().IsMatch(line) && LiteralDefaultFallback().IsMatch(line)).ShouldBe(expected);
}
