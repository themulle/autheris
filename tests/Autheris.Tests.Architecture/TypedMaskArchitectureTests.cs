using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-X0f acceptance: <c>AstSecurityVisitor.BuildDialectMaskExpression</c> (L-9) has no caller outside the engine. Masks reach the
/// string consumers only as rendered <c>MaskSpec</c>s. The scan reads the IL of every Autheris assembly (a text search would miss a
/// different spelling).
/// </summary>
public sealed class TypedMaskArchitectureTests
{
    private static readonly string[] Assemblies =
    [
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly.Location,
        typeof(Autheris.Domain.Interfaces.TableAccessDecision).Assembly.Location,
        typeof(Autheris.Infrastructure.Connectors.SqlConnector).Assembly.Location,
        System.Reflection.Assembly.Load("Autheris.GraphQL").Location,
        System.Reflection.Assembly.Load("Autheris.Api").Location,
        System.Reflection.Assembly.Load("Autheris.Extensions").Location
    ];

    private static List<string> Callers()
    {
        var found = new List<string>();
        foreach (var path in Assemblies.Distinct())
        {
            using var module = ModuleDefinition.ReadModule(path);
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                            instruction.Operand is MethodReference called &&
                            called.Name == "BuildDialectMaskExpression" &&
                            called.DeclaringType.FullName == "TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor")
                        {
                            found.Add($"{module.Assembly.Name.Name}: {type.FullName}.{method.Name}");
                        }
                    }
                }
            }
        }

        return found;
    }

    // Positive control: the scanner resolves the engine method (a rename would otherwise make the scan pass vacuously).
    [Fact]
    public void TheScanner_KnowsTheEngineMethod()
    {
        var engine = typeof(TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor).Assembly;
        engine.GetType("TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor")!
            .GetMethod("BuildDialectMaskExpression")
            .ShouldNotBeNull();
    }

    [Fact]
    public void NoCallerOf_BuildDialectMaskExpression_OutsideTheEngine()
    {
        var callers = Callers();

        callers.ShouldBeEmpty(string.Join(Environment.NewLine, callers));
    }
}
