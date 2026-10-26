using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-15-SC-01: every generated lexer and parser is built by the one factory that binds the current ANTLR cache generation. A direct
/// construction would silently fall back to the process-wide static DFA arrays (unbounded growth).
/// </summary>
public sealed class SqlBaseConstructionArchitectureTests
{
    private const string FactoryFile = "src/TrinoSqlEngine/Parsing/AntlrCacheGenerations.cs";

    private static readonly Regex Construction = new(@"new\s+SqlBase(Lexer|Parser)\s*\(", RegexOptions.CultureInvariant);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static List<string> Constructors() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => Construction.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(RepositoryRoot(), p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    [Trait("Category", "Architecture")]
    public void SqlBaseLexerAndParser_AreConstructedOnlyThroughTheFactory()
    {
        var offenders = Constructors().Where(f => f != FactoryFile).ToList();
        offenders.ShouldBeEmpty("ARCH-MNT-15-T-11: construct lexers and parsers with AntlrCacheGenerations: " + string.Join(", ", offenders));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ConstructionScan_FindsTheFactory_PositiveControl() =>
        Constructors().ShouldContain(FactoryFile);
}
