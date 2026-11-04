using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #447 (AC-ATR-11, SEC-ATR-05, SEC-ATR-12, SEC-ATR-13): coverage guard of the data-source transport gate. Fails on a new
/// DI-scope/Task.Run site, a new executor/connector reference, a remaining reference to the removed fail-open helpers, a
/// choke point that no longer calls the gate, or an allow-all <c>TransportGateFallback</c> installed outside the Unit test
/// assembly. The reviewed lists live in Baselines/ingress-exemptions.txt (changes need explicit security approval).
/// The route half of the baseline is enforced by the integration test IngressTransportCoverageTests.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class TransportGateCoverageTests
{
    private const string BaselineFile = "ingress-exemptions.txt";

    /// <summary>The choke points C1..C5 (plus the aggregation path) that must call the transport gate.</summary>
    private static readonly string[] ChokePointFiles =
    [
        "src/Autheris.Application/Services/GatewayExecutionService.cs",
        "src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs",
        "src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs",
        "src/Autheris.Application/Olap/OlapStagingService.cs",
        "src/Autheris.Application/Olap/FederatedStagingService.cs",
        "src/Autheris.Application/Analytics/GovernedAggregationService.ValidationAndExecution.cs",
        "src/Autheris.Application/Data/Changes/GovernedChangeDetectionService.cs"
    ];

    [GeneratedRegex(@"\b(CreateScope|CreateAsyncScope)\s*\(|\bTask\s*\.\s*Run\s*[(<]")]
    private static partial Regex ScopeSite();

    [GeneratedRegex(@"\b(IDataSourceExecutor|GovernedConnectorReader|ReadRawAsync|ExecuteCoreAsync|IAutherisConnectorRegistry|IAutherisConnectorFactory)\b")]
    private static partial Regex ExecutorReference();

    [GeneratedRegex(@"\b(IsTransportAllowed|EnsureTransportAllowed)\b")]
    private static partial Regex RemovedHelperReference();

    [GeneratedRegex(@"\.\s*Ensure(All|CatalogSources?)?Async\s*\(")]
    private static partial Regex GateEnsureCall();

    [GeneratedRegex(@"TransportGateFallback\s*\.\s*Current\s*(\?\?)?=(?!=)")]
    private static partial Regex FallbackAssignment();

    // The profile method is the gate's own input; every other reference is a bypass of the gate.
    private static readonly string[] ProfileAllowlist =
    [
        "src/Autheris.Domain/Options/DataSources/EffectiveDataSourceProfile.cs",
        "src/Autheris.Application/DataSources/DataSourceTransportGate.cs"
    ];

    [Fact]
    public void SEC_ATR_13_Baseline_EveryEntryHasKnownKindAndReason()
    {
        var bad = Entries().Where(e => e.Kind is not ("route" or "scope" or "executor") || e.Key.Length == 0 || e.Reason.Length < 10).ToList();
        bad.ShouldBeEmpty("Every baseline line is '<route|scope|executor> <key> | <reason>'.");
        Entries().Count(e => e.Kind == "route").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void SEC_ATR_05_SEC_ATR_12_ScopeSites_AreAllReviewed()
    {
        var measured = Measure(RepositoryScan.ProductionFiles, ScopeSite());
        var problems = Evaluate(measured, Keys("scope"));

        problems.ShouldBeEmpty(
            "A new CreateScope/CreateAsyncScope/Task.Run site must copy or stamp the ingress transport (IngressTransportContext) "
            + "or be added to Baselines/ingress-exemptions.txt with security approval (SEC-ATR-05, SEC-ATR-13).");
    }

    [Fact]
    public void SEC_ATR_13_DataExecutionReferences_AreAllReviewed()
    {
        var measured = Measure(RepositoryScan.ProductionFiles, ExecutorReference());
        var problems = Evaluate(measured, Keys("executor"));

        problems.ShouldBeEmpty(
            "A new direct caller of executors/connectors outside the approved list bypasses the choke points (SEC-ATR-13).");
    }

    [Fact]
    public void SEC_ATR_13_NoCallers_OfResolverIsTransportAllowed()
    {
        var offenders = Measure(RepositoryScan.ProductionFiles, RemovedHelperReference())
            .Where(f => !ProfileAllowlist.Contains(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("IsTransportAllowed (resolver) and EnsureTransportAllowed were removed; only IDataSourceTransportGate decides.");
    }

    [Fact]
    public void SEC_ATR_04_SEC_ATR_12_ChokePoints_InvokeTransportGate()
    {
        var withGate = Measure(RepositoryScan.ProductionFiles, GateEnsureCall());

        ChokePointFiles.Where(f => !withGate.Contains(f)).ShouldBeEmpty("Every choke point must call IDataSourceTransportGate.Ensure*Async.");
    }

    [Fact]
    public void SEC_ATR_01_TransportGateFallback_IsNeverSetOutsideUnitTests()
    {
        var unitTests = "tests/Autheris.Tests.Unit/";
        var offenders = AllRepositoryCs()
            .Where(f => !f.RelativePath.StartsWith(unitTests, StringComparison.Ordinal))
            .Where(f => f.RelativePath != "tests/Autheris.Tests.Architecture/TransportGateCoverageTests.cs") // holds the planted samples
            .Where(f => RepositoryScan.CodeLines(f.Text).Any(l => FallbackAssignment().IsMatch(l)))
            .Select(f => f.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "TransportGateFallback stays deny-all in src, Benchmarks and SqlCompilerGate; only Autheris.Tests.Unit installs the allow-all seam.");
    }

    [Fact]
    public void Scanners_DetectPlantedViolations()
    {
        ScopeSite().IsMatch("var s = _sp.CreateAsyncScope();").ShouldBeTrue();
        ScopeSite().IsMatch("await Task.Run(() => Work());").ShouldBeTrue();
        ScopeSite().IsMatch("var s = _sp.CreateScope ();").ShouldBeTrue();
        ScopeSite().IsMatch("var t = Task.Delay(1);").ShouldBeFalse();
        ExecutorReference().IsMatch("private readonly IDataSourceExecutor _e;").ShouldBeTrue();
        ExecutorReference().IsMatch("await reader.ReadRawAsync(x);").ShouldBeTrue();
        RemovedHelperReference().IsMatch("resolver.IsTransportAllowed(t)").ShouldBeTrue();
        RemovedHelperReference().IsMatch("guard.EnsureTransportAllowed(t)").ShouldBeTrue();
        GateEnsureCall().IsMatch("await _transportGate.EnsureCatalogSourceAsync(n, ct);").ShouldBeTrue();
        GateEnsureCall().IsMatch("await g.EnsureAllAsync(names);").ShouldBeTrue();
        GateEnsureCall().IsMatch("await g.EnsureAsync(name);").ShouldBeTrue();
        FallbackAssignment().IsMatch("TransportGateFallback.Current = AllowAllTransportGate.Instance;").ShouldBeTrue();
        FallbackAssignment().IsMatch("TransportGateFallback.Current ??= x;").ShouldBeTrue();
        FallbackAssignment().IsMatch("_g = g ?? TransportGateFallback.Current;").ShouldBeFalse();

        Evaluate(new SortedSet<string> { "src/X/New.cs" }, new HashSet<string>()).ShouldContain(p => p.StartsWith("NEW"));
        Evaluate(new SortedSet<string>(), new HashSet<string> { "src/X/Gone.cs" }).ShouldContain(p => p.StartsWith("STALE"));
        Evaluate(new SortedSet<string> { "src/X/A.cs" }, new HashSet<string> { "src/X/A.cs" }).ShouldBeEmpty();
    }

    private static SortedSet<string> Measure(IEnumerable<RepositoryScan.SourceFile> files, Regex pattern) =>
        new(files.Where(f => RepositoryScan.CodeLines(f.Text).Any(l => pattern.IsMatch(l))).Select(f => f.RelativePath), StringComparer.Ordinal);

    private static List<string> Evaluate(SortedSet<string> measured, HashSet<string> baseline)
    {
        var problems = measured.Where(f => !baseline.Contains(f)).Select(f => "NEW (not reviewed): " + f).ToList();
        problems.AddRange(baseline.Where(f => !measured.Contains(f)).Select(f => "STALE baseline entry (remove it): " + f));
        return problems;
    }

    private static HashSet<string> Keys(string kind) => Entries().Where(e => e.Kind == kind).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

    private static List<(string Kind, string Key, string Reason)> Entries() =>
        RepositoryScan.ReadBaseline(BaselineFile)
            .Select(l =>
            {
                var bar = l.IndexOf('|', StringComparison.Ordinal);
                var head = (bar < 0 ? l : l[..bar]).Trim();
                var reason = bar < 0 ? string.Empty : l[(bar + 1)..].Trim();
                var space = head.IndexOf(' ', StringComparison.Ordinal);
                return space < 0 ? (head, string.Empty, reason) : (head[..space], head[(space + 1)..].Trim(), reason);
            })
            .ToList();

    private static IEnumerable<RepositoryScan.SourceFile> AllRepositoryCs()
    {
        var root = RepositoryScan.RepoRoot;
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = RepositoryScan.Normalize(Path.GetRelativePath(root, path));
            var segments = relative.Split('/');
            if (segments.Any(s => s is "bin" or "obj" or "node_modules" or ".git"))
            {
                continue;
            }

            yield return new RepositoryScan.SourceFile(relative, File.ReadAllText(path));
        }
    }
}
