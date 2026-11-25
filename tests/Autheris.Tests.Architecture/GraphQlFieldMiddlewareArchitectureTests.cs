namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1109: a global field middleware registered as <c>UseField&lt;T&gt;()</c> makes Hot Chocolate compile two expression trees
/// for every object field of the schema. With the per-table catalog types that is tens of thousands of compiles and dominated
/// gateway startup (615 s to readiness at ~4,500 tables, 93 s with a delegate registration). Field middleware is registered as a
/// delegate (<c>UseField(next =&gt; new T(next).InvokeAsync)</c>), never through the generic overload.
/// </summary>
public sealed class GraphQlFieldMiddlewareArchitectureTests
{
    private static readonly Regex GenericUseField = new(@"\.UseField\s*<", RegexOptions.Compiled);

    [Fact]
    public void Field_middleware_is_registered_as_a_delegate_not_through_the_generic_overload()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => GenericUseField.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        offenders.ShouldBeEmpty(
            "Register GraphQL field middleware as UseField(next => new T(next).InvokeAsync); the generic UseField<T>() compiles "
            + "expression trees per schema field (#1109):\n" + string.Join("\n", offenders));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }
}
