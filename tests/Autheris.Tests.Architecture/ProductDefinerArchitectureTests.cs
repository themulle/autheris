using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Data products, definer mode: the definer identity is the only way to skip the consent decision of the base objects of a product definition.
/// Only the product load service may create it, and only the federation product runner may hand it to the federation engine.
/// </summary>
[Trait("Category", "Security")]
public sealed class ProductDefinerArchitectureTests
{
    private const string DefinerType = "Autheris.Application.Products.ProductDefiner";
    private const string LoadService = "Autheris.Application.Products.ProductLoadService";
    private const string Runner = "Autheris.Application.Products.FederationProductQueryRunner";

    private static IEnumerable<ModuleDefinition> ProductionModules()
    {
        yield return CecilModuleCache.For(typeof(Autheris.Application.Products.ProductLoadService).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.GovernanceRepository).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.GraphQL.Types.Mutation).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Api.Security.GatewayAuthSchemes).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Modules.Lakehouse.LakehouseModule).Assembly);
    }

    private static string Owner(TypeDefinition type) => (type.DeclaringType ?? type).FullName;

    private static List<string> Callers(Func<MethodReference, bool> isTarget, string allowedOwner)
    {
        var violations = new List<string>();
        foreach (var module in ProductionModules())
        {
            foreach (var type in module.GetTypes())
            {
                if (string.Equals(Owner(type), allowedOwner, StringComparison.Ordinal) || string.Equals(Owner(type), DefinerType, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && instruction.Operand is MethodReference callee && isTarget(callee))
                        {
                            violations.Add($"{type.FullName}::{method.Name}");
                        }
                    }
                }
            }
        }

        return violations;
    }

    [Fact]
    public void Definer_identity_not_constructible_outside_ProductLoadService()
    {
        var module = CecilModuleCache.For(typeof(Autheris.Application.Products.ProductLoadService).Assembly);
        var definer = module.GetType(DefinerType);
        definer.IsPublic.ShouldBeTrue();
        definer.Methods.Where(m => m.IsConstructor).ShouldAllBe(c => !c.IsPublic && !c.IsFamily && !c.IsFamilyOrAssembly, "the constructor must stay internal");

        var violations = Callers(m => m.Name == ".ctor" && m.DeclaringType.FullName == DefinerType, LoadService);

        violations.ShouldBeEmpty("Only ProductLoadService may create a product definer: " + string.Join("; ", violations));
        module.GetTypes().Where(t => Owner(t) == LoadService).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Any(m => m.Body.Instructions.Any(x => x.Operand is MethodReference r && r.Name == ".ctor" && r.DeclaringType.FullName == DefinerType))
            .ShouldBeTrue("the guard must see the one legitimate call site");
    }

    [Fact]
    public void Definer_execution_is_only_called_by_the_federation_product_runner()
    {
        var violations = Callers(
            m => m.DeclaringType.FullName == "Autheris.Application.Products.IProductDefinerExecutor" && m.Name == "ExecuteAsync", Runner);

        violations.ShouldBeEmpty("Only the federation product runner may run a definition as the definer: " + string.Join("; ", violations));
    }
}
