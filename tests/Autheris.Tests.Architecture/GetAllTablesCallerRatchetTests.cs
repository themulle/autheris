using System.Text.RegularExpressions;
using Shouldly;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Shrink-only caller ratchet preventing new code in <c>src/</c> and <c>modules/</c> from calling
/// <c>GetAllTablesAsync(</c> instead of bounded <c>GetTablesAsync</c>, <c>QueryTablesAsync</c>, or catalog snapshots.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class GetAllTablesCallerRatchetTests(ITestOutputHelper output)
{
    [GeneratedRegex(@"\bGetAllTablesAsync\s*\(")]
    private static partial Regex GetAllTablesCallRegex();

    [GeneratedRegex(@"Task\s*<\s*IReadOnlyList\s*<\s*(?:[\w.]+\.)?TableMetadata\s*>\s*>\s*GetAllTablesAsync\s*\(")]
    private static partial Regex MethodDeclarationRegex();

    [Fact]
    public void NoNewFile_Calls_GetAllTablesAsync()
    {
        var baseline = new HashSet<string>(RepositoryScan.ReadBaseline("get-all-tables-callers.txt"), StringComparer.Ordinal);
        var scanned = EnumerateNonTestSourceFiles();
        var callers = FindCallers(scanned);

        var added = callers.Where(path => !baseline.Contains(path)).ToList();
        var removed = baseline.Where(path => !callers.Contains(path, StringComparer.Ordinal)).ToList();

        foreach (var path in removed)
        {
            output.WriteLine($"HINT (can shrink get-all-tables-callers.txt): {path}");
        }

        added.ShouldBeEmpty(
            "New callers of GetAllTablesAsync( are forbidden; use QueryTablesAsync, GetTablesAsync, or CatalogSnapshotProvider.");
    }

    [Fact]
    public void Scanner_DetectsNewCaller_AndIgnoresDeclarationsAndRepository()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs",
                "public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct) { return []; }"),
            ("src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs",
                "Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default);"),
            ("src/Autheris.Application/Governance/Services/AccessPlanningNullStores.cs",
                "public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);"),
            ("src/Autheris.Api/Endpoints/NewEndpoint.cs",
                "// GetAllTablesAsync( in comment\nvar x = await repo.GetAllTablesAsync(ct);")
        };

        var found = FindCallers(files);
        found.ShouldBe(["src/Autheris.Api/Endpoints/NewEndpoint.cs"]);
    }

    private static IEnumerable<(string Path, string Text)> EnumerateNonTestSourceFiles()
    {
        var srcFiles = RepositoryScan.EnumerateFiles("src", "*.cs");
        var moduleDir = Path.Combine(RepositoryScan.RepoRoot, "modules");
        var moduleFiles = Directory.Exists(moduleDir)
            ? RepositoryScan.EnumerateFiles("modules", "*.cs")
                .Where(p => !p.Contains(".Tests/", StringComparison.Ordinal))
            : [];

        foreach (var path in srcFiles.Concat(moduleFiles))
        {
            yield return (path, RepositoryScan.ReadText(path));
        }
    }

    private static List<string> FindCallers(IEnumerable<(string Path, string Text)> files)
    {
        var callers = new List<string>();
        foreach (var (path, text) in files)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("GovernanceRepository", StringComparison.Ordinal) ||
                string.Equals(path, "src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var hasCall = RepositoryScan.CodeLines(text)
                .Any(line => GetAllTablesCallRegex().IsMatch(line) && !MethodDeclarationRegex().IsMatch(line));
            if (hasCall)
            {
                callers.Add(path);
            }
        }

        callers.Sort(StringComparer.Ordinal);
        return callers;
    }
}
