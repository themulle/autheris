using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-X0d, SEC-X1-02 / SEC-ADG-12: the typed row-policy producers never render a policy value into text. The scan reads the IL
/// (a text search would miss a different spelling) of every type that builds a <c>PolicyPredicate</c> from consent values.
/// The legacy inline string producers stay until X1 removes them (L-list); they are not part of the scanned set.
/// </summary>
public sealed class RowPolicyArchitectureTests
{
    private static readonly string[] Forbidden = ["FormatSafeLiteral", "EscapeSqlLiteral", "FormatLiteralValue"];

    private static readonly string[] TypedProducerTypes =
    [
        "Autheris.Application.Sql.Policy.TypedRowFilterBuilder",
        "Autheris.Application.Sql.Policy.AdvancedRlsPredicateBuilder",
        "Autheris.Application.Sql.Policy.PolicyPredicateRenderer",
        "Autheris.Application.Sql.Policy.PolicyParameterNamer"
    ];

    private static List<string> Callers(ModuleDefinition module, Func<TypeDefinition, bool> typeFilter, Func<MethodDefinition, bool> methodFilter)
    {
        var found = new List<string>();
        foreach (var type in module.GetTypes().Where(typeFilter))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody && methodFilter(m)))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                        instruction.Operand is MethodReference called && Forbidden.Contains(called.Name))
                    {
                        found.Add($"{type.FullName}.{method.Name} -> {called.Name}");
                    }
                }
            }
        }

        return found;
    }

    private static ModuleDefinition Application() =>
        CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);

    // Positive control: the scanner sees the call in a legacy method that really contains it.
    [Fact]
    public void TheScanner_FindsFormatSafeLiteral_InTheLegacyProducer()
    {
        var module = Application();
        Callers(module, t => t.FullName == "Autheris.Application.Services.RowFilterSqlBuilder", _ => true)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void NoFormatSafeLiteral_OnPolicyValuePaths()
    {
        var module = Application();
        var present = module.GetTypes().Select(t => t.FullName).ToHashSet();
        foreach (var type in TypedProducerTypes)
        {
            present.ShouldContain(type, $"{type} must exist: the typed producers are scanned by name");
        }

        // Typed producers (whole types, including compiler-generated closures) ...
        var violations = Callers(module, t => TypedProducerTypes.Any(n => t.FullName == n || t.FullName.StartsWith(n + "/", StringComparison.Ordinal)), _ => true);

        // ... and the typed entry points that live on the shared legacy classes.
        violations.AddRange(Callers(module,
            t => t.FullName is "Autheris.Application.Services.AdvancedRlsFilterGenerator" or "Autheris.Application.Services.RlsFilterGenerator"
                 or "Autheris.Application.Services.RowFilterSqlBuilder",
            m => m.Name is "BuildCorrelatedPredicate" or "BuildCrossSourceSetPredicate" or "BuildCombinedRowFilterParameterized"));

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    // ---- WP-X0g (F-4): the typed virtual filter compiler neither builds an engine nor edits SQL text ----

    private static List<string> CallsInTypedVirtualFilterProducers(ModuleDefinition module, Func<MethodReference, bool> forbidden)
    {
        var found = new List<string>();
        foreach (var type in module.GetTypes().Where(t =>
                     t.FullName.StartsWith("Autheris.Application.VirtualFilters.SqlFilterCompiler", StringComparison.Ordinal) ||
                     t.FullName.StartsWith("Autheris.Application.VirtualFilters.StructuredFilterSqlBuilder", StringComparison.Ordinal)))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody && m.Name.Contains("Typed", StringComparison.Ordinal)))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference called && forbidden(called))
                    {
                        found.Add($"{type.FullName}.{method.Name} -> {called.DeclaringType.Name}.{called.Name}");
                    }
                }
            }
        }

        return found;
    }

    private static bool IsSqlTextEdit(MethodReference called) =>
        (called.DeclaringType.FullName == "System.String" && called.Name is "Replace" or "Insert" or "Remove") ||
        called.DeclaringType.Name == "FastSqlEngine" || called.Name == "GenerateGovernedSql";

    [Fact]
    public void TheScanner_FindsStringReplaceAndTheEngine_InTheLegacyCompile()
    {
        var module = Application();
        var legacy = module.GetTypes().Single(t => t.FullName == "Autheris.Application.VirtualFilters.SqlFilterCompiler")
            .Methods.Single(m => m.Name == "Compile" && m.HasBody);

        legacy.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Any(IsSqlTextEdit).ShouldBeTrue();
    }

    [Fact]
    public void NoStringReplaceOnSql_InTheTypedVirtualFilterProducers()
    {
        var module = Application();
        var typedMethods = module.GetTypes()
            .Where(t => t.FullName.StartsWith("Autheris.Application.VirtualFilters.SqlFilterCompiler", StringComparison.Ordinal) ||
                        t.FullName.StartsWith("Autheris.Application.VirtualFilters.StructuredFilterSqlBuilder", StringComparison.Ordinal))
            .SelectMany(t => t.Methods).Where(m => m.Name.Contains("Typed", StringComparison.Ordinal)).ToList();
        typedMethods.ShouldNotBeEmpty("CompileTyped and BuildTyped must exist: they are scanned by name");

        var violations = CallsInTypedVirtualFilterProducers(module, IsSqlTextEdit);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }
}
