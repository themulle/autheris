using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-01: freezes the csproj ProjectReference graph. Any new or removed edge must be an explicit,
/// reviewed change to Baselines/project-edges.txt (a snapshot of the current graph; test, simulation and tool
/// projects may reference src). Only added edges fail; removed edges are fine.
/// </summary>
public sealed partial class ProjectReferenceGraphArchitectureTests
{
    private static readonly Lazy<IReadOnlyList<string>> AllowedEdges = new(() => RepositoryScan.ReadBaseline("project-edges.txt"));

    [GeneratedRegex(@"<ProjectReference\s+Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferenceInclude();

    [Fact]
    public void ProjectGraph_MatchesAllowlist()
    {
        var edges = ScanRepository();

        var added = edges.Except(AllowedEdges.Value, StringComparer.Ordinal).ToList();

        added.ShouldBeEmpty("New project references must be reviewed and added to Baselines/project-edges.txt:\n" + string.Join("\n", added));
    }

    [Fact]
    public void ProjectGraph_HasNoCycles()
    {
        var cycle = FindCycle(ScanRepository());

        cycle.ShouldBeNull("Project reference cycle: " + cycle);
    }

    [Fact]
    public void SrcProjects_DoNotReferenceTestOrToolProjects()
    {
        var violations = ScanRepository()
            .Where(IsSrcToNonSrcEdge)
            .ToList();

        violations.ShouldBeEmpty("src projects must not reference tests, tools or benchmarks:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Scanner_FindsTheKnownProjects()
    {
        var edges = ScanRepository();

        edges.Count.ShouldBeGreaterThan(40);
        edges.ShouldContain("Autheris.Api->Autheris.Application");
    }

    [Fact]
    public void Scanner_DetectsPlantedEdge()
    {
        var planted = ExtractEdges(
            "src/Autheris.Domain/Autheris.Domain.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"..\\..\\tests\\Autheris.Tests.Common\\Autheris.Tests.Common.csproj\" /></ItemGroup></Project>");

        planted.ShouldBe(["Autheris.Domain->Autheris.Tests.Common"]);
        planted.Except(AllowedEdges.Value, StringComparer.Ordinal).ShouldNotBeEmpty();
        // A removed edge is not an added edge, so shrinking the graph passes.
        AllowedEdges.Value.Skip(1).Except(AllowedEdges.Value, StringComparer.Ordinal).ShouldBeEmpty();
        planted.Any(IsSrcToNonSrcEdge).ShouldBeTrue("A src project referencing a test project must be flagged.");
    }

    [Fact]
    public void CycleDetector_DetectsPlantedCycle()
    {
        FindCycle(["A->B", "B->C", "C->A"]).ShouldNotBeNull();
        FindCycle(["A->B", "B->C", "A->C"]).ShouldBeNull();
    }

    private static bool IsSrcToNonSrcEdge(string edge)
    {
        var parts = edge.Split("->");
        return SrcProjectNames.Value.Contains(parts[0]) && !SrcProjectNames.Value.Contains(parts[1]);
    }

    private static readonly Lazy<HashSet<string>> SrcProjectNames = new(() =>
        RepositoryScan.EnumerateFiles("src", "*.csproj")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.Ordinal));

    private static List<string> ScanRepository()
    {
        var edges = new List<string>();
        var projects = new List<string>();
        foreach (var root in new[] { "src", "tests", "tools", "benchmarks" })
        {
            projects.AddRange(RepositoryScan.EnumerateFiles(root, "*.csproj"));
        }

        projects.Count.ShouldBeGreaterThan(20, "Scanner found too few csproj files; refusing to pass vacuously.");
        foreach (var project in projects)
        {
            edges.AddRange(ExtractEdges(project, RepositoryScan.ReadText(project)));
        }

        return edges.Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractEdges(string projectPath, string content)
    {
        var from = Path.GetFileNameWithoutExtension(projectPath);
        return ProjectReferenceInclude().Matches(content)
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
            .Select(to => $"{from}->{to}")
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();
    }

    private static string? FindCycle(IEnumerable<string> edges)
    {
        var graph = edges
            .Select(e => e.Split("->"))
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Select(p => p[1]).ToList());
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        string? Visit(string node, Stack<string> path)
        {
            state[node] = 1;
            path.Push(node);
            foreach (var next in graph.GetValueOrDefault(node) ?? [])
            {
                var s = state.GetValueOrDefault(next);
                if (s == 1)
                {
                    return string.Join(" -> ", path.Reverse().Append(next));
                }

                if (s == 0 && Visit(next, path) is { } found)
                {
                    return found;
                }
            }

            path.Pop();
            state[node] = 2;
            return null;
        }

        foreach (var node in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(node) == 0 && Visit(node, new Stack<string>()) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }
}
