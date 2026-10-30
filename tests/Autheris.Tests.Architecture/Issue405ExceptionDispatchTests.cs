using System.Text.RegularExpressions;
using Autheris.Application.Analytics;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>#405 I405-SC-06: the unsealed <c>SqlCompileNotSupportedException</c> keeps one subtype and is never dispatched on by exact type.</summary>
[Trait("Category", "Security")]
public class Issue405ExceptionDispatchTests
{
    private static readonly string[] KnownMappers =
    [
        Path.Combine("src", "Autheris.Api", "Endpoints", "AnalyticsEndpoints.cs"),
        Path.Combine("src", "Autheris.GraphQL", "Catalog", "AggregateErrors.cs"),
        Path.Combine("src", "Autheris.Application", "Sql", "Errors", "SqlCompilerErrorMapper.cs")
    ];

    [Fact]
    public void I405_T_S05_Architecture_SingleSubclass_NoExactTypeDispatch()
    {
        var derived = AppDomain.CurrentDomain.GetAssemblies()
            .Concat(new[] { typeof(SqlCompileNotSupportedException).Assembly, typeof(AggregationGroupKeyNotSupportedException).Assembly })
            .Distinct()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    return e.Types.Where(t => t != null)!;
                }
            })
            .Where(t => t != null && t.BaseType == typeof(SqlCompileNotSupportedException))
            .Select(t => t!.FullName)
            .Distinct()
            .ToList();
        derived.ShouldBe([typeof(AggregationGroupKeyNotSupportedException).FullName]);

        var root = FindRepositoryRoot();
        var exact = new Regex(@"GetType\(\)\s*(==|!=|\.Equals)|typeof\(\s*(TrinoSqlEngine\.)?SqlCompileNotSupportedException\s*\)|is\s+exactly", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                KnownMappers.Contains(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains("SqlCompileNotSupportedException", StringComparison.Ordinal) && exact.IsMatch(text))
            {
                offenders.Add(relative);
            }
        }

        offenders.ShouldBeEmpty("Never dispatch on the exact type of SqlCompileNotSupportedException; a subclass must map like the base.");

        foreach (var mapper in KnownMappers)
        {
            var text = File.ReadAllText(Path.Combine(root, mapper));
            Regex.IsMatch(text, @"GetType\(\)\s*(==|!=)|typeof\(\s*(TrinoSqlEngine\.)?SqlCompileNotSupportedException\s*\)").ShouldBeFalse(mapper);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Autheris.sln) not found.");
    }
}
