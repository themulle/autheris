using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #107, SEC-RF107-10 (a): the correlated filter site registry is process-global. A writer outside the typed builder could mark an
/// arbitrary node as a site, so <c>Register</c> has exactly one call site in <c>src/</c> and the registry type is not public. The scan reads
/// the IL of every production assembly (a text search would miss another spelling).
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetRegistryTests
{
    private const string RegistryType = "Autheris.Application.Sql.Policy.RowFilterKeySets.CorrelatedFilterSiteRegistry";
    private const string AllowedCaller = "Autheris.Application.Sql.Policy.AdvancedRlsPredicateBuilder.CorrelatedExpression";

    private static IEnumerable<ModuleDefinition> ProductionModules() =>
    [
        CecilModuleCache.For(typeof(Autheris.Domain.Common.TableIdentifier).Assembly),
        CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly),
        CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly),
        CecilModuleCache.For(typeof(Autheris.GraphQL.Catalog.GraphQlTreeBuilder).Assembly),
        CecilModuleCache.For(typeof(Autheris.Api.Security.ClaimsNormalizer).Assembly),
        CecilModuleCache.For(typeof(TrinoSqlEngine.FastSqlEngine).Assembly)
    ];

    private static string TopLevel(TypeDefinition type) => type.DeclaringType is null ? type.FullName : TopLevel(type.DeclaringType);

    private static List<string> CallersOf(string declaringType, string method) =>
        ProductionModules()
            .SelectMany(module => module.GetTypes())
            .SelectMany(type => type.Methods.Where(m => m.HasBody).Select(m => (Type: type, Method: m)))
            .Where(m => m.Method.Body.Instructions.Any(i =>
                i.OpCode.Code is Code.Call or Code.Callvirt or Code.Ldftn &&
                i.Operand is MethodReference called && called.DeclaringType.FullName == declaringType && called.Name == method))
            .Select(m => $"{TopLevel(m.Type)}.{(m.Type.DeclaringType is null ? m.Method.Name : "<nested>")}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void RegisterIsCalledOnlyByAdvancedRlsPredicateBuilder()
    {
        var callers = CallersOf(RegistryType, "Register");

        callers.ShouldBe([AllowedCaller], "CorrelatedFilterSiteRegistry.Register may only be called from AdvancedRlsPredicateBuilder.CorrelatedExpression: " + string.Join(", ", callers));
    }

    [Fact]
    public void TheRegistryIsNotPublic()
    {
        var type = CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly)
            .GetTypes().Single(t => t.FullName == RegistryType);

        type.IsNotPublic.ShouldBeTrue();
        type.IsAbstract.ShouldBeTrue();   // static class
        type.IsSealed.ShouldBeTrue();
    }

    [Fact]
    public void TheScanner_FindsACallSite_PositiveControl() =>
        CallersOf("TrinoSqlEngine.Governance.PolicyPredicate", "Create").ShouldNotBeEmpty();
}
