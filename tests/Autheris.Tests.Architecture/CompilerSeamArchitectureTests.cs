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

    // CR-ADG-38: production code passes only DmlGuardOptions.Strict. Building a different value needs the constructor, a with-copy
    // (<Clone>$) or an init setter; none may be called outside the options type itself, nor may GovernancePolicy.Dml be assigned.
    private static List<string> CallersOfDmlGuardCustomisation(string assemblyPath)
    {
        var callers = new List<string>();
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (var type in module.GetTypes().Where(t => t.FullName != "TrinoSqlEngine.DmlGuardOptions"))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || instruction.Operand is not MethodReference called) continue;
                    string owner = called.DeclaringType.FullName;
                    bool guardOptions = owner == "TrinoSqlEngine.DmlGuardOptions" &&
                                        (called.Name == ".ctor" || called.Name == "<Clone>$" || called.Name.StartsWith("set_", StringComparison.Ordinal));
                    bool policyAssignment = owner == "TrinoSqlEngine.GovernancePolicy" && called.Name == "set_Dml";
                    if (guardOptions || policyAssignment) callers.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        return callers;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static TrinoSqlEngine.DmlGuardOptions ControlRelaxesAGuard() =>
        TrinoSqlEngine.DmlGuardOptions.Strict with { RejectUnfilteredDml = false };

    [Fact]
    public void TheDmlGuardScanner_FindsARelaxedGuard_InAKnownCaller() =>
        CallersOfDmlGuardCustomisation(typeof(CompilerSeamArchitectureTests).Assembly.Location)
            .ShouldContain(c => c.EndsWith(nameof(ControlRelaxesAGuard), StringComparison.Ordinal));

    [Fact]
    public void ProductionCode_PassesOnlyTheStrictDmlGuardOptions()
    {
        var violations = new List<string>();
        foreach (var assembly in ProductionAssemblies)
        {
            violations.AddRange(CallersOfDmlGuardCustomisation(assembly.Location).Select(c => $"{assembly.GetName().Name}: {c}"));
        }

        violations.ShouldBeEmpty("DML guards are fixed (CR-ADG-38); production code must use DmlGuardOptions.Strict only: " + string.Join(", ", violations));
    }

    // CR-ADG-43: a statement with a row-count check runs only through CheckedDmlExecutor. The binders refuse it, and the one internal
    // entry that binds it (BindForCheckedExecution) may be called only by that executor. No production assembly may add a binder
    // that is not derived from DbCommandCompiledSqlBinder (it would not carry the refusal).
    private static List<string> CallersOfCheckedBinding(string assemblyPath)
    {
        var callers = new List<string>();
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (var type in module.GetTypes())
        {
            string top = (type.DeclaringType ?? type).FullName;   // the async state machine is a nested type of its owner
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) || instruction.Operand is not MethodReference called) continue;
                    if (called.Name == "BindForCheckedExecution" && called.DeclaringType.FullName == "TrinoSqlEngine.Ast.Emit.DbCommandCompiledSqlBinder")
                    {
                        callers.Add(top);
                    }
                }
            }
        }

        return callers.Distinct().ToList();
    }

    [Fact]
    public void OnlyTheCheckedDmlExecutor_BindsAStatementWithARowCountCheck()
    {
        var callers = new List<string>();
        foreach (var assembly in ProductionAssemblies)
        {
            callers.AddRange(CallersOfCheckedBinding(assembly.Location));
        }

        // positive control: the scanner does see the one legitimate caller
        callers.ShouldBe(new[] { "TrinoSqlEngine.Ast.Emit.CheckedDmlExecutor" });
    }

    [Fact]
    public void EveryProductionBinder_DerivesFromTheRefusingBaseClass()
    {
        var violations = new List<string>();
        foreach (var assembly in ProductionAssemblies)
        {
            using var module = ModuleDefinition.ReadModule(assembly.Location);
            foreach (var type in module.GetTypes().Where(t => !t.IsInterface && !t.IsAbstract))
            {
                bool implementsBinder = type.Interfaces.Any(i => i.InterfaceType.FullName == "TrinoSqlEngine.Ast.Emit.ICompiledSqlBinder");
                var baseTypes = new List<string>();
                for (var b = type.BaseType; b is not null; b = b.Resolve()?.BaseType) baseTypes.Add(b.FullName);
                bool derives = baseTypes.Contains("TrinoSqlEngine.Ast.Emit.DbCommandCompiledSqlBinder");
                if ((implementsBinder || derives) && !derives) violations.Add(type.FullName);
            }
        }

        violations.ShouldBeEmpty("A binder must derive from DbCommandCompiledSqlBinder (CR-ADG-43): " + string.Join(", ", violations));
    }
}
