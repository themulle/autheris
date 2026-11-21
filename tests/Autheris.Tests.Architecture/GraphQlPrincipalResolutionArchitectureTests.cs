namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GraphQlPrincipalResolutionArchitectureTests
{
    private static readonly Regex HttpContextUserRead = new(@"\b[Hh]ttp[Cc]ontext(?:\?\.)?\.User\b", RegexOptions.Compiled);
    private static readonly Regex IsInRoleRead = new(@"\.IsInRole\s*\(", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Security/GraphQlCallerPrincipal.cs",
        "Subscriptions/WebSocketAuthInterceptor.cs",
        "Federation/SubgraphSecurityDelegatingHandler.cs"
    };

    [Fact]
    public void GraphQl_resolvers_use_shared_caller_principal_helper()
    {
        var graphqlRoot = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.GraphQL");
        Directory.Exists(graphqlRoot).ShouldBeTrue();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(graphqlRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(graphqlRoot, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                AllowedFiles.Contains(rel))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (HttpContextUserRead.IsMatch(line) || IsInRoleRead.IsMatch(line))
                {
                    violations.Add($"{rel}:{i + 1}: {line.Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty();
    }
}
