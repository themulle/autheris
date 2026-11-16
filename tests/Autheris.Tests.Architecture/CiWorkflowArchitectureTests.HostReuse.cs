namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

public sealed partial class CiWorkflowArchitectureTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Integration_test_constructors_do_not_call_WithWebHostBuilder()
    {
        var allowlist = RepositoryScan.ReadBaseline("per-test-host-allowlist.txt")
            .ToHashSet(StringComparer.Ordinal);
        allowlist.Count.ShouldBeLessThanOrEqualTo(0, "per-test-host-allowlist.txt is shrink-only and must not exceed 0 entries.");

        string[] migratedSix =
        [
            "tests/Autheris.Tests.Integration/RowFilterEvaluationEndpointAuthTests.cs",
            "tests/Autheris.Tests.Integration/DefaultTenantIngressHttpTests.cs",
            "tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs",
            "tests/Autheris.Tests.Integration/Issue341GraphQlInternalErrorTests.cs",
            "tests/Autheris.Tests.Integration/WalkingSkeletonIntegrationTests.cs",
            "tests/Autheris.Tests.Integration/QueryDefenseStressTests.cs",
        ];
        foreach (var migrated in migratedSix)
        {
            allowlist.ShouldNotContain(migrated, $"{migrated} was migrated in #520 and must not appear in per-test-host-allowlist.txt.");
        }

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.EnumerateFiles("tests/Autheris.Tests.Integration", "*.cs"))
        {
            var source = RepositoryScan.ReadText(file);
            if (HasPerTestClassHostAntiPattern(source))
            {
                actual.Add(RepositoryScan.ToRelativePath(file));
            }
        }

        var unallowlisted = actual.Where(f => !allowlist.Contains(f)).ToList();
        unallowlisted.ShouldBeEmpty(
            "Integration test classes must use a shared IntegrationHost fixture instead of IClassFixture<WebApplicationFactory<Program>> or calling WithWebHostBuilder in the test constructor:\n"
            + string.Join("\n", unallowlisted));

        var staleAllowlistEntries = allowlist.Where(f => !actual.Contains(f)).ToList();
        staleAllowlistEntries.ShouldBeEmpty(
            "Shrink per-test-host-allowlist.txt by removing migrated files:\n" + string.Join("\n", staleAllowlistEntries));

        // Negative self-check: planted constructor WithWebHostBuilder is caught.
        HasPerTestClassHostAntiPattern("""
            public sealed class BadTests : IClassFixture<WebApplicationFactory<Program>>
            {
                private readonly WebApplicationFactory<Program> _factory;
                public BadTests(WebApplicationFactory<Program> factory) => _factory = factory.WithWebHostBuilder(_ => {});
            }
            """).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Created_factories_are_disposed()
    {
        var allowlist = RepositoryScan.ReadBaseline("per-test-host-allowlist.txt")
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var file in RepositoryScan.EnumerateFiles("tests/Autheris.Tests.Integration", "*.cs"))
        {
            var rel = RepositoryScan.ToRelativePath(file);
            if (allowlist.Contains(rel))
            {
                continue;
            }

            var source = RepositoryScan.ReadText(file);
            foreach (var v in FindUndisposedFactoryCreations(source, rel))
            {
                violations.Add(v);
            }
        }

        violations.ShouldBeEmpty(
            "Every created WebApplicationFactory<Program>, IntegrationHost, or WithWebHostBuilder result in non-allowlisted integration tests must be in a using/await using, returned by a helper to a using caller, or stored in a disposed field/property:\n"
            + string.Join("\n", violations));

        // Negative self-check: planted undisposed WithWebHostBuilder in a test method is caught.
        FindUndisposedFactoryCreations("""
            public sealed class SampleTests
            {
                [Fact]
                public void LeakedHost()
                {
                    var leaked = _factory.WithWebHostBuilder(b => { });
                    using var client = leaked.CreateClient();
                }
            }
            """, "SampleTests.cs").ShouldNotBeEmpty();
    }

    private static bool HasPerTestClassHostAntiPattern(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var baseTypes = cls.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [];
            if (baseTypes.Any(t => t.Contains("IClassFixture<WebApplicationFactory<Program>>", StringComparison.Ordinal)))
            {
                return true;
            }

            bool isFixtureClass = cls.Identifier.Text.EndsWith("Fixture", StringComparison.Ordinal)
                                  || baseTypes.Any(t => t.Contains("IntegrationHost", StringComparison.Ordinal) || t.Contains("WebApplicationFactory", StringComparison.Ordinal));
            if (isFixtureClass)
            {
                continue;
            }

            foreach (var ctor in cls.Members.OfType<ConstructorDeclarationSyntax>())
            {
                if (ctor.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsWithWebHostBuilderCall))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> FindUndisposedFactoryCreations(string source, string fileLabel)
    {
        var violations = new List<string>();
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            if (!IsTopLevelFactoryCreation(node))
            {
                continue;
            }

            if (!IsProperlyOwnedOrDisposed(node))
            {
                var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                violations.Add($"{fileLabel}:{line}: {node.ToString().Split('\n')[0].Trim()}");
            }
        }

        return violations;
    }

    private static bool IsTopLevelFactoryCreation(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax inv && IsWithWebHostBuilderCall(inv))
        {
            if (inv.Parent is MemberAccessExpressionSyntax parentAccess &&
                parentAccess.Parent is InvocationExpressionSyntax parentInv &&
                IsWithWebHostBuilderCall(parentInv))
            {
                return false;
            }

            return true;
        }

        if (node is BaseObjectCreationExpressionSyntax creation)
        {
            var typeName = creation switch
            {
                ObjectCreationExpressionSyntax oce => oce.Type.ToString(),
                ImplicitObjectCreationExpressionSyntax ioce => InferImplicitCreationTypeName(ioce),
                _ => string.Empty,
            };

            if (typeName.Contains("WebApplicationFactory<Program>", StringComparison.Ordinal) ||
                typeName.Contains("IntegrationHost", StringComparison.Ordinal))
            {
                if (creation.Parent is MemberAccessExpressionSyntax ma &&
                    ma.Parent is InvocationExpressionSyntax outerInv &&
                    IsWithWebHostBuilderCall(outerInv))
                {
                    return false;
                }

                return true;
            }
        }

        return false;
    }

    private static string InferImplicitCreationTypeName(ImplicitObjectCreationExpressionSyntax creation)
    {
        for (var p = creation.Parent; p is not null; p = p.Parent)
        {
            switch (p)
            {
                case VariableDeclarationSyntax vd:
                    return vd.Type.ToString();
                case PropertyDeclarationSyntax pd:
                    return pd.Type.ToString();
                case MethodDeclarationSyntax md:
                    return md.ReturnType.ToString();
            }
        }

        return string.Empty;
    }

    private static bool IsWithWebHostBuilderCall(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax ma &&
        ma.Name.Identifier.Text == "WithWebHostBuilder";

    private static bool IsProperlyOwnedOrDisposed(SyntaxNode expr)
    {
        for (var p = expr.Parent; p is not null; p = p.Parent)
        {
            switch (p)
            {
                case UsingStatementSyntax:
                    return true;
                case LocalDeclarationStatementSyntax localDecl when !localDecl.UsingKeyword.IsKind(SyntaxKind.None):
                    return true;
                case LocalDeclarationStatementSyntax localDecl:
                {
                    var method = localDecl.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var varName = localDecl.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                    if (method is not null && varName is not null)
                    {
                        if (method.DescendantNodes().OfType<ReturnStatementSyntax>()
                            .Any(r => r.Expression is IdentifierNameSyntax id && id.Identifier.Text == varName))
                        {
                            return true;
                        }

                        if (method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Any(i => i.Expression is MemberAccessExpressionSyntax m &&
                                      m.Expression is IdentifierNameSyntax id &&
                                      id.Identifier.Text == varName &&
                                      m.Name.Identifier.Text is "Dispose" or "DisposeAsync"))
                        {
                            return true;
                        }
                    }

                    return false;
                }
                case ReturnStatementSyntax:
                case ArrowExpressionClauseSyntax when p.Parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax:
                    return true;
                case FieldDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case AssignmentExpressionSyntax:
                {
                    var enclosingClass = p.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    if (enclosingClass is null)
                    {
                        return false;
                    }

                    var baseTypes = enclosingClass.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [];
                    return baseTypes.Any(t =>
                        t.Contains("IDisposable", StringComparison.Ordinal) ||
                        t.Contains("IAsyncLifetime", StringComparison.Ordinal) ||
                        t.Contains("IAsyncDisposable", StringComparison.Ordinal) ||
                        t.Contains("IntegrationHost", StringComparison.Ordinal) ||
                        t.Contains("WebApplicationFactory", StringComparison.Ordinal));
                }
            }
        }

        return false;
    }
}
