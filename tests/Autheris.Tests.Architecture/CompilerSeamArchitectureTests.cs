using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// CR-ADG-20: production code must never enable an experimental dialect. The check reads the IL of every production assembly
/// (a call to the <c>CompileRequest.AllowExperimentalDialect</c> setter, in an object initializer or a <c>with</c> expression), so
/// a variable, a constant or a different spelling cannot slip past it the way a text search could.
/// </summary>
public sealed class CompilerSeamArchitectureTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly,
        typeof(TrinoSqlEngine.FastSqlEngine).Assembly
    ];

    private const string Setter = "set_AllowExperimentalDialect";
    private const string Owner = "TrinoSqlEngine.CompileRequest";

    /// <summary>Methods of <paramref name="assemblyPath"/> that call the setter.</summary>
    private static List<string> CallersOfTheSetter(string assemblyPath)
    {
        var callers = new List<string>();
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                        instruction.Operand is MethodReference called &&
                        called.Name == Setter && called.DeclaringType.FullName == Owner)
                    {
                        callers.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        return callers;
    }

    // Positive control: the scanner must see a real call (otherwise a rename would silently turn the test green).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static bool ControlSetsTheFlag(TrinoSqlEngine.CompileRequest request) =>
        (request with { AllowExperimentalDialect = true }).AllowExperimentalDialect;

    [Fact]
    public void TheScanner_FindsAnAllowExperimentalDialectSetter_InAKnownCaller()
    {
        var callers = CallersOfTheSetter(typeof(CompilerSeamArchitectureTests).Assembly.Location);
        callers.ShouldContain(c => c.EndsWith(nameof(ControlSetsTheFlag), StringComparison.Ordinal));
    }

    [Fact]
    public void AllowExperimentalDialect_IsNeverSetByProductionCode()
    {
        var violations = new List<string>();
        foreach (var assembly in ProductionAssemblies)
        {
            foreach (var caller in CallersOfTheSetter(assembly.Location))
            {
                violations.Add($"{assembly.GetName().Name}: {caller}");
            }
        }

        violations.ShouldBeEmpty("AllowExperimentalDialect is a test-only switch; production code must not set it: " + string.Join(", ", violations));
    }
}
