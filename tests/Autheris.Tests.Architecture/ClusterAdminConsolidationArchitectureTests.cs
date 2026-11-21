using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class ClusterAdminConsolidationArchitectureTests
{
    private static readonly Regex IsInRoleClusterAdminRegex = new(
        @"\.IsInRole\s*\(\s*""ClusterAdmin""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex RawClusterAdminCompareRegex = new(
        @"(?:string\.Equals\([^)]*""ClusterAdmin""|""ClusterAdmin""\s*,\s*StringComparison|\.\s*Contains\(\s*""ClusterAdmin"")",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedRawCompareFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/Autheris.Domain/Security/ClusterAdminPolicy.cs",
        "src/Autheris.Domain/Security/SecurityPrincipalContext.cs",
        "src/Autheris.Api/Security/SecurityContextFactory.cs",
    };

    [Fact]
    public void No_isInRole_ClusterAdmin_in_src()
    {
        var offenders = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            foreach (var line in RepositoryScan.CodeLines(file.Text))
            {
                if (IsInRoleClusterAdminRegex.IsMatch(line))
                {
                    offenders.Add(file.RelativePath);
                    break;
                }
            }
        }

        offenders.ShouldBeEmpty("Use ClusterAdminPolicy.IsCanonicalClusterAdmin instead of User.IsInRole(\"ClusterAdmin\").");
    }

    [Fact]
    public void Cross_tenant_gates_use_canonical_cluster_admin()
    {
        var endpointSecurity = RepositoryScan.ReadText("src/Autheris.Api/Endpoints/EndpointSecurity.cs");
        var routeMatchStart = endpointSecurity.IndexOf("RequireTenantRouteMatch", StringComparison.Ordinal);
        routeMatchStart.ShouldBeGreaterThan(0);
        var routeMatchBlock = endpointSecurity.Substring(routeMatchStart, Math.Min(600, endpointSecurity.Length - routeMatchStart));
        routeMatchBlock.ShouldContain("IsCanonicalClusterAdmin(");
        routeMatchBlock.ShouldNotContain("IsClusterAdmin(");

        var grantRecords = RepositoryScan.ReadText("src/Autheris.Api/Endpoints/ConsentWorkflowAdminEndpoints.GrantRecords.cs");
        grantRecords.ShouldContain("IsCanonicalClusterAdmin(");
        grantRecords.ShouldNotContain("EndpointSecurity.IsClusterAdmin(");

        var offenders = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (AllowedRawCompareFiles.Contains(file.RelativePath))
            {
                continue;
            }

            foreach (var line in RepositoryScan.CodeLines(file.Text))
            {
                if (RawClusterAdminCompareRegex.IsMatch(line))
                {
                    offenders.Add(file.RelativePath);
                    break;
                }
            }
        }

        offenders.ShouldBeEmpty("Route ClusterAdmin checks through ClusterAdminPolicy instead of raw string comparisons.");
    }
}
