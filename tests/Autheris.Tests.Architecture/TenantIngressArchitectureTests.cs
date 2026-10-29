using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>WP-DT-4 (DT-SC-03, DT-SC-05): non-HTTP principal construction sites and the informational tenant-source claim.</summary>
[Trait("Category", "Security")]
public sealed class TenantIngressArchitectureTests
{
    // file -> number of "new ClaimsPrincipal(" constructions. A new site fails the build: it must stamp a resolved tenant from a captured
    // caller context or be unresolved by construction (it then throws TenantUnresolvedException on tenant use), and be added here.
    private static readonly Dictionary<string, int> Allowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Autheris.Api/Endpoints/DuckDbOlapEndpoints.cs"] = 1,                      // empty fallback: unresolved
        ["src/Autheris.Api/Security/BasicAuthSession.cs"] = 1,                          // ingress (claims transformation stamps)
        ["src/Autheris.Api/Security/BasicAuthenticationHandler.cs"] = 1,                // ingress
        ["src/Autheris.Api/Security/ForwardAuthAuthenticationHandler.cs"] = 1,          // ingress
        ["src/Autheris.Api/Security/JwtSocketTokenValidator.cs"] = 1,                   // ingress (WebSocket, same transformation)
        ["src/Autheris.Api/Security/ScimAuthenticationHandler.cs"] = 1,                 // ingress (realm binding)
        ["src/Autheris.Api/Security/TenantStamping.cs"] = 1,                            // wraps the identity the resolver reads (claims transformation helper)
        ["src/Autheris.Api/Security/TestAuthHandler.cs"] = 2,                           // test-host ingress
        ["src/Autheris.Application/Events/Services/CdcSubscriptionPrincipal.cs"] = 1,   // stored subscription tenant (invalid fails closed)
        ["src/Autheris.Application/Governance/Analysis/QueryAnalysisService.cs"] = 1,   // simulated target: the analysed caller's tenant
        ["src/Autheris.Application/Jobs/Services/AsyncQueryJobManager.cs"] = 4,         // job audit principals: the captured job tenant
        ["src/Autheris.Application/Mcp/Services/McpProtocolHandler.cs"] = 1,            // MCP session: the session tenant
        ["src/Autheris.Application/Services/GatewayExecutionService.cs"] = 1,           // dev anonymous: default tenant or unresolved
        ["src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs"] = 1,          // empty fallback: unresolved
        ["src/Autheris.Domain/Model/CatalogApiModels.cs"] = 2,                          // empty principal: unresolved
        ["src/Autheris.GraphQL/Catalog/AggregateGraphQlTypes.cs"] = 1,                  // empty fallback: unresolved
        ["src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs"] = 1,               // empty fallback: unresolved
        ["src/Autheris.GraphQL/Catalog/MetricGraphQlTypes.cs"] = 1,                     // empty fallback: unresolved
        ["src/Autheris.GraphQL/DynamicTypes/DynamicTableType.cs"] = 1,                  // empty fallback: unresolved
        ["src/Autheris.GraphQL/Mcp/GatewayMcpQueryExecutor.cs"] = 1,                    // MCP session tenant
        ["src/Autheris.GraphQL/Types/QueryTypes.cs"] = 2                                // empty fallback: unresolved
    };

    [Fact]
    public void NonHttp_Principal_Sites_Allowlist_Is_Exact()
    {
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var count = Regex.Count(file.Text, @"new\s+(System\.Security\.Claims\.)?ClaimsPrincipal\s*\(");
            if (count > 0)
            {
                actual[file.RelativePath] = count;
            }
        }

        actual.ShouldBe(new SortedDictionary<string, int>(Allowlist, StringComparer.Ordinal));
    }

    private static readonly string[] TenantSourceReaders =
    [
        "src/Autheris.Domain/Tenancy/TenantSourceClaim.cs",
        "src/Autheris.Api/Security/TenantStamping.cs",
        "src/Autheris.Api/Security/SecurityContextFactory.cs",
        "src/Autheris.Domain/Security/SecurityPrincipalContext.cs"
    ];

    [Fact]
    public void Tenant_Source_Is_Never_Read_By_Authorization()
    {
        var offenders = RepositoryScan.ProductionFiles
            .Where(f => f.Text.Contains("TenantSourceClaim", StringComparison.Ordinal) || f.Text.Contains("autheris:tenant_source", StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .Where(p => !TenantSourceReaders.Contains(p, StringComparer.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty("autheris:tenant_source is informational (telemetry, SecurityPrincipalContext.TenantSource) and never an authorization input.");
    }
}
