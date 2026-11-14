using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// #668: <c>WebSql:DataSourceMappings</c> is applied by one resolver (<c>SqlDataSourceResolver</c>). The transport gate, the allowlist
/// guard, the catalog provider, federation and the profile resolver call it; none of them keeps a private lookup copy.
/// </summary>
[Trait("Category", "Security")]
public sealed class DataSourceMappingResolverArchitectureTests
{
    private const string ResolverFile = "Autheris.Application/Sql/Services/SqlDataSourceResolver.cs";

    /// <summary>A lookup of one mapping (TryGetValue, ContainsKey, indexer); enumerating the whole dictionary is not a lookup.</summary>
    private static readonly Regex Lookup = new(@"DataSourceMappings\s*(\?|!)?\s*(\.\s*(TryGetValue|ContainsKey|GetValueOrDefault)\b|\[)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static string Src()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return Path.Combine(dir.FullName, "src");
    }

    [Fact]
    public void TransportGate_And_AllowlistGuard_ShareResolver()
    {
        var src = Src();
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: f, Rel: Path.GetRelativePath(src, f).Replace('\\', '/')))
            .Where(x => !x.Rel.Contains("/obj/", StringComparison.Ordinal) && !x.Rel.Contains("/bin/", StringComparison.Ordinal))
            .Where(x => x.Rel != ResolverFile)
            .Where(x => Lookup.IsMatch(File.ReadAllText(x.File)))
            .Select(x => x.Rel)
            .ToList();

        offenders.ShouldBeEmpty("Only SqlDataSourceResolver may look up WebSql:DataSourceMappings; call SqlDataSourceResolver.ResolveConnectionKey instead.");
    }

    [Theory]
    [InlineData("options.WebSql.DataSourceMappings.TryGetValue(name, out var target)")]
    [InlineData("webSql?.DataSourceMappings.ContainsKey(name)")]
    [InlineData("webSql.DataSourceMappings[name]")]
    public void Pattern_RecognisesTheLookupForms(string sample)
    {
        // guards the pattern: a renamed property or a broken regex would turn the test above into a test that can never fail
        Lookup.IsMatch(sample).ShouldBeTrue();
    }
}
