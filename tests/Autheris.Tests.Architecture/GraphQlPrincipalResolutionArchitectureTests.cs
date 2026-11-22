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

        AllowedFiles.Any(p => p.StartsWith("Mcp/", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Mcp_executor_uses_shared_helper()
    {
        var executorPath = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.GraphQL", "Mcp", "GatewayMcpQueryExecutor.cs");
        File.Exists(executorPath).ShouldBeTrue();

        var source = File.ReadAllText(executorPath);
        source.ShouldContain("GraphQlCallerPrincipal.BuildMcpSessionPrincipal");
        source.ShouldContain("GraphQlCallerPrincipal.SetMcpSessionState");

        var forbiddenGlobalStatePattern = new Regex(
            @"\.(AddGlobalState|SetGlobalState|TryAddGlobalState)\s*\(\s*(?:""(?:ClaimsPrincipal|CallerSecurityContext)""|nameof\s*\(\s*(?:ClaimsPrincipal|CallerSecurityContext)\s*\))",
            RegexOptions.CultureInvariant);

        var forbiddenConstructionPattern = new Regex(
            @"\bnew\s+(?:Autheris\.Domain\.Common\.)?CallerSecurityContext\s*\(",
            RegexOptions.CultureInvariant);

        var violations = new List<string>();
        var lines = File.ReadAllLines(executorPath);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (forbiddenGlobalStatePattern.IsMatch(line) || forbiddenConstructionPattern.IsMatch(line))
            {
                violations.Add($"Mcp/GatewayMcpQueryExecutor.cs:{i + 1}: {line.Trim()}");
            }
        }

        violations.ShouldBeEmpty();
    }
}
