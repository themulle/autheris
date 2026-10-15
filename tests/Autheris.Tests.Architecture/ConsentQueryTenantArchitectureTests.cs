using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-SC-01 (WP-SC-01): every request path loads consents with a tenant predicate. The tenant-less overloads of
/// <c>IConsentRepository.GetActiveConsentsForSubjectsAsync</c> / <c>GetAllActiveConsentsForSubjectsAsync</c> remain for repository
/// forwarding and tests only; no production code outside the repositories may call them.
/// </summary>
public sealed class ConsentQueryTenantArchitectureTests
{
    private static readonly string[] Guarded = ["GetActiveConsentsForSubjectsAsync", "GetAllActiveConsentsForSubjectsAsync"];

    private static IEnumerable<ModuleDefinition> ProductionModules()
    {
        yield return CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.GraphQL.Types.Mutation).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Api.Security.GatewayAuthSchemes).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly);
    }

    private static bool IsTenantLess(MethodReference m) =>
        Guarded.Contains(m.Name) && m.Parameters.All(p => !p.ParameterType.FullName.Contains("TenantId", StringComparison.Ordinal));

    [Fact]
    public void NoProductionCode_CallsTheTenantLessConsentQueryOverloads()
    {
        var violations = new List<string>();
        foreach (var module in ProductionModules())
        {
            foreach (var type in module.GetTypes())
            {
                if (type.FullName.Contains("GovernanceRepository", StringComparison.Ordinal))
                {
                    continue; // the repositories forward the tenant-less overloads to the tenant-aware ones
                }

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference callee && IsTenantLess(callee))
                        {
                            violations.Add($"{type.FullName}::{method.Name} -> {callee.Name}");
                        }
                    }
                }
            }
        }

        violations.ShouldBeEmpty("Consent queries on request paths must pass the tenant: " + string.Join("; ", violations));
    }

    [Fact]
    public void TheGuardRecognisesTheTenantAwareOverload()
    {
        var module = CecilModuleCache.For(typeof(Autheris.Application.Interfaces.IConsentRepository).Assembly);
        var iface = module.GetType("Autheris.Application.Interfaces.IConsentRepository");
        var guarded = iface.Methods.Where(m => Guarded.Contains(m.Name)).ToList();

        guarded.Count(m => IsTenantLess(m)).ShouldBe(2);
        guarded.Count(m => !IsTenantLess(m)).ShouldBe(2);
    }
}
