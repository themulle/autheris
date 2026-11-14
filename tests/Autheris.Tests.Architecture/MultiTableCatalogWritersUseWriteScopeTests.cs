using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// A type that upserts catalog tables or relations in a loop must open a catalog write scope, otherwise every table publishes its own
/// catalog version bump and catalog:changed event (O(n) snapshot, search and schema rebuilds). Single-table writers are listed in
/// Baselines/multi-table-catalog-writers.txt; the list may only shrink.
/// </summary>
public sealed partial class MultiTableCatalogWritersUseWriteScopeTests
{
    private const string BaselineFile = "multi-table-catalog-writers.txt";

    [GeneratedRegex(@"\b(UpsertTableMetadataAsync|CreateRelationAsync)\s*\(")]
    private static partial Regex CatalogWriteCall();

    [GeneratedRegex(@"\b(foreach|while)\s*\(|\bfor\s*\(")]
    private static partial Regex LoopKeyword();

    [GeneratedRegex(@"\bBeginOrNoop\s*\(|\bICatalogWriteScopeFactory\b[\s\S]*?\.Begin\s*\(")]
    private static partial Regex OpensScope();

    private static bool IsCodeLine(string line)
    {
        var trimmed = line.TrimStart();
        return !(trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal));
    }

    internal static bool NeedsScopeButHasNone(string text)
    {
        var lines = text.Split('\n').Where(IsCodeLine).ToList();
        var code = string.Join('\n', lines);
        return CatalogWriteCall().IsMatch(code) && LoopKeyword().IsMatch(code) && !OpensScope().IsMatch(code);
    }

    private static HashSet<string> Baseline() =>
        RepositoryScan.ReadBaseline(BaselineFile)
            .Select(l => l.Split('|')[0].Trim())
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Types_that_upsert_catalog_tables_in_a_loop_open_a_write_scope()
    {
        var baseline = Baseline();
        var offenders = RepositoryScan.ProductionFiles
            .Where(f => NeedsScopeButHasNone(f.Text))
            .Select(f => f.RelativePath)
            .ToList();

        offenders.Except(baseline, StringComparer.Ordinal)
            .ShouldBeEmpty("A writer that upserts several tables must open ICatalogWriteScopeFactory.Begin(...) (or BeginOrNoop) around the loop.");
    }

    [Fact]
    public void Baseline_has_no_stale_entries()
    {
        var baseline = Baseline();
        var offenders = RepositoryScan.ProductionFiles
            .Where(f => NeedsScopeButHasNone(f.Text))
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        baseline.Except(offenders, StringComparer.Ordinal)
            .ShouldBeEmpty("These baseline entries no longer need the exemption; remove them from " + BaselineFile + ".");
    }

    [Fact]
    public void Scanner_detects_a_planted_unscoped_loop_and_accepts_a_scoped_one()
    {
        const string unscoped = "foreach (var t in tables)\n{\n    await repo.UpsertTableMetadataAsync(t, ct);\n}\n";
        const string scoped = "await using var s = factory.BeginOrNoop(\"x\");\nforeach (var t in tables)\n{\n    await repo.UpsertTableMetadataAsync(t, ct);\n}\n";
        const string commentOnly = "// foreach (var t in tables) repo.UpsertTableMetadataAsync(t, ct);\n";

        NeedsScopeButHasNone(unscoped).ShouldBeTrue();
        NeedsScopeButHasNone(scoped).ShouldBeFalse();
        NeedsScopeButHasNone(commentOnly).ShouldBeFalse();
    }
}
