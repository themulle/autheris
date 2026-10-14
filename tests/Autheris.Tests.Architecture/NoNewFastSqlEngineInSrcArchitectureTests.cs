using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-ARC-C-11 (ARC-S-04): production code reuses one engine (parser pool, compile cache, worker pool) instead of constructing a
/// <c>FastSqlEngine</c> per call. Only the engine's own static default may exist; two static engines with a tighter query-length limit are
/// listed explicitly.
/// </summary>
public sealed class NoNewFastSqlEngineInSrcArchitectureTests
{
    private static readonly Regex Construction = new(@"new\s+(TrinoSqlEngine\.)?FastSqlEngine\s*(\(|\{)", RegexOptions.CultureInvariant);

    // Static single-instance engines with a deliberate MaxQueryLength; they are constructed once per process.
    private static readonly string[] AllowedFiles =
    [
        "src/Autheris.Application/Streaming/Services/StreamingRowFilterAstEvaluator.cs",
        "src/Autheris.Application/Sql/SqlSecurityValidator.cs"
    ];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static List<string> Constructors(string folder) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => Construction.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(RepositoryRoot(), p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void NoNewFastSqlEngineInSrc()
    {
        var offenders = Constructors("src").Where(f => !AllowedFiles.Contains(f)).ToList();
        offenders.ShouldBeEmpty("Use FastSqlEngine.Default or the injected ISqlEngine instead of constructing an engine: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ConstructionScan_FindsKnownConstructions_PositiveControl() =>
        Constructors("tests/TrinoSqlEngine.Tests").ShouldNotBeEmpty();
}
