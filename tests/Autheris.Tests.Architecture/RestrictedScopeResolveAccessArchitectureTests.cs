using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// REQ-GOV-143 (SEC-GOV143-02/-10/-14, T-GOV143-21): a restricted-scope holder is only recognised when the resolver is given the token scopes.
/// Every production caller of <c>IConsentResolutionService.ResolveAccess</c> therefore passes the scope set of the claims overload
/// (<c>TokenAccessScope.GetScopeSet(IEnumerable&lt;Claim&gt;)</c>, which also reads a plain <c>scope</c> claim) or applies
/// <c>RestrictedScopeClearance.Unknown</c>; none reads <c>TokenAccessScope.GetScopes</c> (scp only) or the principal overload, and none omits the
/// argument. A new caller that does not follow the rule fails the build.
/// </summary>
public sealed class RestrictedScopeResolveAccessArchitectureTests
{
    /// <summary>Types that forward a scope set they receive as a parameter (their callers are covered by the same rule) or implement the resolver.</summary>
    private static readonly HashSet<string> ScopeParameterForwarders = new(StringComparer.Ordinal)
    {
        "Autheris.Application.Services.CatalogColumnAccessResolver",
        "Autheris.Application.Services.ConsentResolutionService"
    };

    /// <summary>The delegated-token test of D-7 / the Entra policy keep <c>GetScopes</c> on purpose (a plain <c>scope</c> claim must not look delegated).</summary>
    private static readonly HashSet<string> GetScopesAllowed = new(StringComparer.Ordinal)
    {
        "Autheris.Api.Security.EntraTokenPolicy",
        "Autheris.Application.Governance.ConsentRequests.ConsentDecisionGuard",
        "Autheris.Application.Governance.ConsentRequests.DecisionPerson"
    };

    private static IEnumerable<ModuleDefinition> ProductionModules()
    {
        yield return CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.GraphQL.Types.Mutation).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Api.Security.GatewayAuthSchemes).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly);
    }

    private sealed record Usage(
        bool CallsResolveAccess,
        bool CallsGetScopeSetClaims,
        bool CallsGetScopeSetPrincipal,
        bool CallsGetScopes,
        bool AppliesUnknown,
        bool OmitsScopeArgument,
        bool CallsCatalogResolver);

    private static string TopLevelName(TypeDefinition type)
    {
        while (type.DeclaringType != null)
        {
            type = type.DeclaringType;
        }

        return type.FullName;
    }

    private static bool IsResolveAccess(MethodReference m) =>
        m.Name == "ResolveAccess" && m.Parameters.Count == 8 &&
        m.DeclaringType.FullName == "Autheris.Domain.Interfaces.IConsentResolutionService";

    private static Dictionary<string, Usage> Scan()
    {
        var usage = new Dictionary<string, Usage>(StringComparer.Ordinal);
        foreach (var module in ProductionModules())
        {
            foreach (var type in module.GetTypes())
            {
                var key = TopLevelName(type);
                var u = usage.TryGetValue(key, out var existing) ? existing : new Usage(false, false, false, false, false, false, false);
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    var il = method.Body.Instructions;
                    for (var i = 0; i < il.Count; i++)
                    {
                        if (il[i].OpCode.Code is not (Code.Call or Code.Callvirt) || il[i].Operand is not MethodReference callee)
                        {
                            continue;
                        }

                        var declaring = callee.DeclaringType.FullName;
                        if (IsResolveAccess(callee))
                        {
                            u = u with { CallsResolveAccess = true };

                            // The omitted optional argument is compiled as a plain ldnull right before the call; a conditional
                            // (`scopes.Count > 0 ? scopes : null`) has the branch of the then-arm in front of its ldnull.
                            if (i >= 2 && il[i - 1].OpCode.Code == Code.Ldnull && il[i - 2].OpCode.Code is not (Code.Br or Code.Br_S))
                            {
                                u = u with { OmitsScopeArgument = true };
                            }
                        }
                        else if (declaring == "Autheris.Domain.Security.TokenAccessScope" && callee.Name == "GetScopeSet")
                        {
                            var paramType = callee.Parameters[0].ParameterType.FullName;
                            u = paramType.Contains("Claim", StringComparison.Ordinal) && paramType.Contains("IEnumerable", StringComparison.Ordinal)
                                ? u with { CallsGetScopeSetClaims = true }
                                : u with { CallsGetScopeSetPrincipal = true };
                        }
                        else if (declaring == "Autheris.Domain.Security.TokenAccessScope" && callee.Name == "GetScopes")
                        {
                            u = u with { CallsGetScopes = true };
                        }
                        else if (declaring == "Autheris.Domain.Model.RestrictedScopeClearance" && callee.Name == "Unknown")
                        {
                            u = u with { AppliesUnknown = true };
                        }
                        else if (declaring == "Autheris.Application.Services.CatalogColumnAccessResolver")
                        {
                            u = u with { CallsCatalogResolver = true };
                        }
                    }
                }

                usage[key] = u;
            }
        }

        return usage;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_21_EveryResolveAccessCaller_PassesClaimsScopesOrUnknown()
    {
        var violations = new List<string>();
        foreach (var (type, u) in Scan().Where(kv => kv.Value.CallsResolveAccess))
        {
            if (ScopeParameterForwarders.Contains(type))
            {
                continue;
            }

            if (!u.CallsGetScopeSetClaims && !u.AppliesUnknown)
            {
                violations.Add($"{type}: resolves access without the claims scope set and without RestrictedScopeClearance.Unknown");
            }

            if (u.OmitsScopeArgument)
            {
                violations.Add($"{type}: omits the callerScopes argument of ResolveAccess");
            }

            if (u.CallsGetScopes || u.CallsGetScopeSetPrincipal)
            {
                violations.Add($"{type}: reads scopes with GetScopes / GetScopeSet(ClaimsPrincipal) instead of the claims overload");
            }
        }

        violations.ShouldBeEmpty("Every ResolveAccess caller must hand the claims scope set to the resolver (REQ-GOV-143): " + string.Join("; ", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProductionCode_ReadsScopesOnlyThroughTheClaimsOverload_ExceptTheDelegatedTokenTests()
    {
        var violations = Scan()
            .Where(kv => (kv.Value.CallsGetScopes || kv.Value.CallsGetScopeSetPrincipal) && !GetScopesAllowed.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        violations.ShouldBeEmpty("GetScopes reads 'scp' only; use TokenAccessScope.GetScopeSet(principal.Claims): " + string.Join("; ", violations));
    }

    [Fact]
    public void TheScan_FindsTheKnownCallers()
    {
        var scan = Scan();

        foreach (var known in new[]
                 {
                     "Autheris.Application.Policy.TableAccessPolicy",
                     "Autheris.Application.Streaming.Services.StreamRlsPolicyEnforcer",
                     "Autheris.Application.Governance.Services.EffectiveAccessExplanationService",
                     "Autheris.Application.Analytics.GovernedAggregationService",
                     "Autheris.Application.Analytics.SemanticMetricQueryService",
                     "Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService"
                 })
        {
            scan.ShouldContainKey(known);
            scan[known].CallsResolveAccess.ShouldBeTrue(known);
            (scan[known].CallsGetScopeSetClaims || scan[known].AppliesUnknown).ShouldBeTrue(known);
        }

        scan["Autheris.Application.Policy.TableAccessPolicy"].AppliesUnknown.ShouldBeTrue();
        scan["Autheris.Api.Security.EntraTokenPolicy"].CallsGetScopes.ShouldBeTrue();
    }
}
