namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-SIMPL-32, SEC-SIMPL-62: verifies that no enforcement, security, or redaction options were deleted while still wired or referenced.
/// </summary>
public sealed class OptionsAuditTests
{
    private static readonly Regex SecurityKeyword = new(
        @"Enforce|Require|Allowed|Allow|Deny|Mask|Pii|Tls|Auth|Redaction",
        RegexOptions.CultureInvariant);

    private const int RemovedOptionsCount = 93;

    [Fact]
    [Trait("Category", "Security")]
    public void No_enforcement_option_deleted_while_wired()
    {
        var removed = RepositoryScan.ReadBaseline("removed-options.txt");
        // Pinned: deleting a row would silently stop checking that option; raise this only when you add a row.
        removed.Count.ShouldBe(RemovedOptionsCount, "The removed-options baseline lists every deleted option; raise the count only when you add a row.");

        var assemblies = new[]
        {
            typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            typeof(Autheris.Infrastructure.Persistence.GovernanceRepository).Assembly,
            typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
            typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
            typeof(Program).Assembly
        };

        var typesByName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var a in assemblies)
        {
            foreach (var t in a.GetTypes())
            {
                if (t.FullName is not null)
                {
                    typesByName.TryAdd(t.FullName, t);
                }
            }
        }

        var productionFiles = RepositoryScan.ProductionFiles;

        foreach (var entry in removed)
        {
            var relative = entry.StartsWith("Autheris.", StringComparison.Ordinal) ? entry["Autheris.".Length..] : entry;
            if (!SecurityKeyword.IsMatch(relative))
            {
                continue;
            }

            var dot = entry.LastIndexOf('.');
            var typeName = entry[..dot];
            var propertyName = entry[(dot + 1)..];

            // 1. Assert property no longer exists on the type (if type still exists)
            if (typesByName.TryGetValue(typeName, out var type))
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                prop.ShouldBeNull($"Enforcement/security option {entry} still exists on {typeName}.");
            }

            // 2. Assert no src/**/*.cs file references .PropertyName
            var memberAccessRegex = new Regex(@"\.\s*" + Regex.Escape(propertyName) + @"\b", RegexOptions.CultureInvariant);
            foreach (var file in productionFiles)
            {
                var text = Regex.Replace(file.Text, @"//[^\n]*|/\*.*?\*/|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""", " ", RegexOptions.Singleline);
                memberAccessRegex.IsMatch(text).ShouldBeFalse(
                    $"Enforcement/security option {entry} is still referenced as .{propertyName} in {file.RelativePath}.");
            }
        }
    }

    /// <summary>
    /// Opt-in switches of kept features are never removed to meet the surface budget: each exists, defaults to off and is still read
    /// by production code. A switch that a later work package has not declared yet may be absent, but once declared it must default to off.
    /// </summary>
    public static TheoryData<string, bool, string> OptInSwitches => new()
    {
        { "SqlEndpoints:Procedures:Enabled", true, @"Procedures\??\.Enabled" },
        { "WebSql:Enabled", true, @"WebSql\??\.Enabled" },
        { "WebSql:AllowDml", true, @"\.AllowDml\b" },
        { "Authentication:ForwardAuth:Enabled", true, @"ForwardAuth\??\.Enabled" },
        { "WebSql:Dml:AllowDeleteGrants", false, @"\.AllowDeleteGrants\b" },
        { "Federation:Enabled", true, @"Federation\??\.Enabled" },
        { "Catalog:Enabled", true, @"Catalog\??\.Enabled" },
        { "CustomQueryPoller:Enabled", true, @"CustomQueryPollerOptions[\s\S]*?\.Enabled\b" },
        { "GraphQL:Subscriptions:Enabled", true, @"Subscriptions\??\.Enabled" },
        { "GraphQL:SlowLists:Enabled", true, @"slowListOptions\.Enabled\b" },
        { "Mcp:Enabled", true, @"Mcp\??\.Enabled" },
        { "WebSql:CrossSource:Enabled", true, @"CrossSource\??\.Enabled" },
        { "WebSql:CrossSource:CoLocation:Enabled", true, @"CoLocation\??\.Enabled" },
        { "Caching:Redis:Enabled", true, @"Redis\??\.Enabled" },
        { "TableCache:Enabled", true, @"TableCacheOptions.*Enabled\b" },
        { "SchemaContracts:Enabled", true, @"SchemaContracts\??\.Enabled" },
        { "Authentication:BasicAuth:Enabled", true, @"(BasicAuth|basic)\??\.Enabled" },
        { "Authentication:BasicAuth:Session:Enabled", true, @"Session\??\.Enabled" },
        { "Authentication:EntraId:Enabled", true, @"EntraId\??\.Enabled" },
        { "Authentication:Adfs:Enabled", true, @"Adfs\??\.Enabled" },
    };

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(OptInSwitches))]
    public void Opt_in_switches_retained(string path, bool mustBeDeclared, string consumerPattern)
    {
        var (property, instance) = ResolveGatewayOption(path);

        if (property is null || instance is null)
        {
            mustBeDeclared.ShouldBeFalse($"Opt-in switch Gateway:{path} was deleted. Opt-in switches of kept features stay.");
            return;
        }

        property.PropertyType.ShouldBe(typeof(bool));
        instance.ShouldBe(false, $"Opt-in switch Gateway:{path} must default to off.");

        var consumer = new Regex(consumerPattern, RegexOptions.CultureInvariant);
        RepositoryScan.ProductionFiles
            .Any(f => !f.RelativePath.StartsWith("src/Autheris.Domain/Options/", StringComparison.Ordinal) &&
                      consumer.IsMatch(StripComments(f.Text)))
            .ShouldBeTrue($"Opt-in switch Gateway:{path} is no longer read by production code, so it no longer gates its feature.");
    }

    /// <summary>
    /// WP-SIMPL-32 PR 0: every <c>*Enabled</c> and <c>bool Allow*</c> option flag in core <c>src/</c> must have a reviewed
    /// decision in <c>tools/options-inventory/flag-decisions.csv</c>.
    /// </summary>
    [Fact]
    public void Every_enabled_flag_has_a_decision()
    {
        var csvPath = Path.Combine(RepositoryScan.RepoRoot, "tools", "options-inventory", "flag-decisions.csv");
        File.Exists(csvPath).ShouldBeTrue("Missing flag decision matrix: tools/options-inventory/flag-decisions.csv");

        var validDecisions = new HashSet<string>(StringComparer.Ordinal) { "keep opt-in", "unconditional", "delete", "module" };
        var validPrs = new HashSet<string>(StringComparer.Ordinal) { "PR 1", "PR 2", "PR 3", "PR 4", "PR 5", "PR 6" };

        var lines = File.ReadAllLines(csvPath);
        lines.Length.ShouldBeGreaterThan(1, "flag-decisions.csv must contain header and decision rows.");
        lines[0].Trim().ShouldBe("class,property,default,security,decision,reason,owning_pr,file");

        var csvKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            var cols = raw.Split(',');
            cols.Length.ShouldBe(8, $"Malformed CSV row {i + 1} (expected 8 columns; avoid unescaped commas in reason): {raw}");

            var cls = cols[0].Trim();
            var prop = cols[1].Trim();
            var decision = cols[4].Trim();
            var reason = cols[5].Trim();
            var owningPr = cols[6].Trim();
            var file = cols[7].Trim();

            cls.ShouldNotBeNullOrWhiteSpace($"Row {i + 1}: class is required.");
            prop.ShouldNotBeNullOrWhiteSpace($"Row {i + 1}: property is required.");
            validDecisions.ShouldContain(decision, $"Row {i + 1} ({cls}.{prop}): invalid decision '{decision}'.");
            reason.ShouldNotBeNullOrWhiteSpace($"Row {i + 1} ({cls}.{prop}): reason is required.");
            validPrs.ShouldContain(owningPr, $"Row {i + 1} ({cls}.{prop}): invalid owning_pr '{owningPr}'.");
            file.ShouldNotBeNullOrWhiteSpace($"Row {i + 1} ({cls}.{prop}): file is required.");

            var key = $"{file}|{cls}|{prop}";
            csvKeys.Add(key).ShouldBeTrue($"Duplicate row in flag-decisions.csv for {cls}.{prop} in {file}.");
        }

        var discovered = DiscoverFlags();
        var missing = discovered.Where(k => !csvKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = csvKeys.Where(k => !discovered.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty($"Undecided flags in src/ missing from flag-decisions.csv: {string.Join(", ", missing)}");
        extra.ShouldBeEmpty($"Stale rows in flag-decisions.csv no longer present in src/: {string.Join(", ", extra)}");
    }

    private static (PropertyInfo? Property, object? Instance) ResolveGatewayOption(string path)
    {
        var segments = path.Split(':');
        object? instance = new Autheris.Domain.Options.GatewayOptions();
        var startIndex = 0;

        var firstProp = instance.GetType().GetProperty(segments[0], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (firstProp is null)
        {
            var sideBoundSection = $"Gateway:{segments[0]}";
            var sideBound = Autheris.Api.Configuration.GatewayConfigurationSchema.SideBound
                .FirstOrDefault(s => string.Equals(s.Path, sideBoundSection, StringComparison.OrdinalIgnoreCase));
            if (sideBound.Type is null)
            {
                return (null, null);
            }

            instance = Activator.CreateInstance(sideBound.Type);
            startIndex = 1;
        }

        PropertyInfo? property = null;
        for (var i = startIndex; i < segments.Length; i++)
        {
            if (instance is null)
            {
                break;
            }

            property = instance.GetType().GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                break;
            }

            instance = property.GetValue(instance);
        }

        return (property, instance);
    }

    private static HashSet<string> DiscoverFlags()
    {
        var enabledRegex = new Regex(@"public\s+bool\s+(\w*Enabled)\s*\{", RegexOptions.CultureInvariant);
        var allowRegex = new Regex(@"public\s+(?:required\s+)?(?:virtual\s+)?bool\s+(Allow\w*)\s*\{", RegexOptions.CultureInvariant);
        var typeRegex = new Regex(@"\b(?:class|record|struct|interface)\s+(\w+)", RegexOptions.CultureInvariant);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var lines = Regex.Split(file.Text, "\r\n|\r|\n");
            var stack = new List<(string Name, int Depth)>();
            var depth = 0;
            string? pending = null;

            foreach (var line in lines)
            {
                if (ProgramRatchetTests.IsCommentLine(line))
                {
                    continue;
                }

                var type = typeRegex.Match(line);
                if (type.Success && !line[..type.Index].Contains('='))
                {
                    pending = type.Groups[1].Value;
                }

                var currentClass = stack.Count > 0 ? stack[^1].Name : "?";

                var enabledMatch = enabledRegex.Match(line);
                if (enabledMatch.Success)
                {
                    keys.Add($"{file.RelativePath}|{currentClass}|{enabledMatch.Groups[1].Value}");
                }

                if (stack.Count > 0 && currentClass.EndsWith("Options", StringComparison.Ordinal))
                {
                    var allowMatch = allowRegex.Match(line);
                    if (allowMatch.Success)
                    {
                        keys.Add($"{file.RelativePath}|{currentClass}|{allowMatch.Groups[1].Value}");
                    }
                }

                foreach (var ch in line)
                {
                    if (ch == '{')
                    {
                        depth++;
                        if (pending is not null)
                        {
                            stack.Add((pending, depth));
                            pending = null;
                        }
                    }
                    else if (ch == '}')
                    {
                        if (stack.Count > 0 && stack[^1].Depth == depth)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        depth--;
                    }
                }
            }
        }

        return keys;
    }

    private static string StripComments(string text) =>
        Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
}
