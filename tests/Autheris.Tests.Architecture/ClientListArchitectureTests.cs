using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// REQ-SQL-128 (SEC-128-11): list element slots are created in one place and the list pass runs in one place, before the security injection.
/// IL scans like the cutover guards, each with a positive control.
/// </summary>
public sealed class ClientListArchitectureTests
{
    private static readonly Assembly Engine = typeof(TrinoSqlEngine.FastSqlEngine).Assembly;

    private static readonly Assembly[] Production =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly,
        Engine
    ];

    /// <summary>Callers (as "Type.Method") of the constructor or a setter of <paramref name="typeName"/> in the assembly.</summary>
    private static List<string> Callers(Assembly assembly, string typeName, Func<MethodReference, bool> matches)
    {
        var callers = new List<string>();
        var module = CecilModuleCache.For(assembly.Location);
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || instruction.Operand is not MethodReference called) continue;
                    if (called.DeclaringType.FullName == typeName && matches(called))
                    {
                        callers.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        return callers.Distinct().ToList();
    }

    private static List<string> AllCallers(string typeName, Func<MethodReference, bool> matches) =>
        Production.SelectMany(a => Callers(a, typeName, matches).Select(c => $"{a.GetName().Name}: {c}")).ToList();

    [Fact]
    public void TheListExpander_IsCreatedOnlyByTheCompiler_InTheListsPass()
    {
        var callers = AllCallers("TrinoSqlEngine.Ast.Visitors.ClientListExpander", c => c.Name == ".ctor");

        callers.ShouldNotBeEmpty("positive control: the scanner sees the compiler's own call");
        callers.ShouldAllBe(c => c.Contains("TrinoSqlEngine.GovernedSqlCompiler.", StringComparison.Ordinal),
            "ClientListExpander may only be created by GovernedSqlCompiler (SEC-128-11): " + string.Join(", ", callers));
    }

    [Fact]
    public void BoundParameters_AreCreatedOnlyByTheEmitterAndTheTemplateRebind()
    {
        // The list element slots (ClientNamed with a ListIndex) come from SqlEmitterContext.BindClient; CompiledSqlTemplate copies them on a cache hit.
        var callers = AllCallers("TrinoSqlEngine.Ast.Emit.BoundParameter", c => c.Name == ".ctor");

        callers.ShouldContain(c => c.Contains("SqlEmitterContext", StringComparison.Ordinal), "positive control");
        callers.ShouldAllBe(c => c.Contains("TrinoSqlEngine.Ast.Buffer.SqlEmitterContext", StringComparison.Ordinal)
                                 || c.Contains("TrinoSqlEngine.Ast.Emit.CompiledSqlTemplate", StringComparison.Ordinal),
            "Only the emitter and the template rebind may create a BoundParameter (SEC-128-11): " + string.Join(", ", callers));
    }

    [Fact]
    public void ListIndexIsAssignedOnlyByTheListPass()
    {
        // `with { ListIndex = ... }` or an init of the member outside the list pass would mint an element slot without the position proof.
        var callers = AllCallers("TrinoSqlEngine.Ast.Nodes.ParameterReference", c => c.Name is "set_ListIndex" or "set_ListCount");
        callers.ShouldBeEmpty("ListIndex and ListCount are only set through the constructor of the list pass: " + string.Join(", ", callers));
    }
}
