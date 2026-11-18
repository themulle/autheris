namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Shouldly;
using Xunit;

public sealed class NoSqliteArchitectureTests
{
    private static readonly Assembly[] ProductionAssemblies = LoadProductionAssemblies();

    private static Assembly[] LoadProductionAssemblies()
    {
        var root = RepositoryScan.RepoRoot;
        var names = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "modules"), "*.csproj", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !Path.GetFileNameWithoutExtension(p).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            .Select(p => XDocument.Load(p).Descendants("AssemblyName").FirstOrDefault()?.Value.Trim() ?? Path.GetFileNameWithoutExtension(p))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return names.Select(n => Assembly.Load(new AssemblyName(n))).ToArray();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_sqlite_types_in_src()
    {
        var typeViolations = new List<string>();
        var refViolations = new List<string>();

        foreach (var assembly in ProductionAssemblies)
        {
            var assemblyName = assembly.GetName().Name ?? assembly.FullName ?? "<unknown>";

            foreach (var type in assembly.GetTypes())
            {
                if ((type.FullName ?? type.Name).Contains("sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    typeViolations.Add($"{assemblyName}: {type.FullName}");
                }
            }

            foreach (var referenced in assembly.GetReferencedAssemblies())
            {
                if ((referenced.Name ?? string.Empty).Contains("sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    refViolations.Add($"{assemblyName} -> {referenced.Name}");
                }
            }

            var module = CecilModuleCache.For(assembly);
            foreach (var typeDef in module.GetTypes())
            {
                foreach (var method in typeDef.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.Operand is MemberReference memberRef &&
                            (memberRef.DeclaringType?.FullName ?? string.Empty).Contains("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase))
                        {
                            refViolations.Add($"{assemblyName}: {typeDef.FullName}.{method.Name} -> {memberRef.FullName}");
                        }
                    }
                }
            }
        }

        typeViolations.ShouldBeEmpty("Production assemblies must not define SQLite types.");
        refViolations.ShouldBeEmpty("Production assemblies must not reference Microsoft.Data.Sqlite.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sqlite_literals_only_in_allowlisted_guards()
    {
        var allowlist = RepositoryScan.ReadBaseline("sqlite-literals.txt")
            .ToHashSet(StringComparer.Ordinal);

        var sourceFiles = RepositoryScan.EnumerateFiles("src", "*.cs")
            .Concat(RepositoryScan.EnumerateFiles("modules", "*.cs"))
            .OrderBy(p => p, StringComparer.Ordinal);

        var hits = new SortedSet<string>(StringComparer.Ordinal);
        var unexpected = new List<string>();

        foreach (var relativePath in sourceFiles)
        {
            var text = RepositoryScan.ReadText(relativePath);
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (token.Kind() is SyntaxKind.StringLiteralToken
                    or SyntaxKind.Utf8StringLiteralToken
                    or SyntaxKind.SingleLineRawStringLiteralToken
                    or SyntaxKind.MultiLineRawStringLiteralToken
                    or SyntaxKind.InterpolatedStringTextToken &&
                    token.ValueText.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(relativePath);
                    if (!allowlist.Contains(relativePath))
                    {
                        var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        unexpected.Add($"{relativePath}:{line}: {token.ValueText}");
                    }
                }
            }
        }

        unexpected.ShouldBeEmpty(
            "SQLite string literals in src/ and modules/ are restricted to allowlisted guards in Baselines/sqlite-literals.txt:\n" +
            string.Join("\n", unexpected));

        var stale = allowlist.Where(path => !hits.Contains(path)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        stale.ShouldBeEmpty(
            "Baselines/sqlite-literals.txt contains stale entries with no SQLite string literals:\n" +
            string.Join("\n", stale));
    }

    [Fact]
    public void Src_files_do_not_mention_sqlite_outside_allowlist()
    {
        var allowlist = RepositoryScan.ReadBaseline("sqlite-literals.txt")
            .ToHashSet(StringComparer.Ordinal);

        var offenders = RepositoryScan.EnumerateFiles("src", "*.cs")
            .Where(p => !allowlist.Contains(p))
            .Where(p => RepositoryScan.ReadText(p).Contains("sqlite", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "src/ files outside Baselines/sqlite-literals.txt must not mention SQLite, not even in comments:\n" +
            string.Join("\n", offenders));
    }
}
