using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-X0l acceptance (SEC-ADG-02, SEC-X1-04): every decision site hands the typed policy producers to <c>TableAccessPolicy</c>, and the
/// assembly of the compiler policy never touches the string policy providers or the mutable <c>RlsOptions</c> bag. The scans read IL.
/// </summary>
public sealed class GovernancePolicyArchitectureTests
{
    private static readonly string[] Assemblies =
    [
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly.Location,
        typeof(Autheris.Infrastructure.Connectors.SqlConnector).Assembly.Location,
        System.Reflection.Assembly.Load("Autheris.GraphQL").Location,
        System.Reflection.Assembly.Load("Autheris.Api").Location,
        System.Reflection.Assembly.Load("Autheris.Extensions").Location
    ];

    private const string TableAccessPolicyName = "Autheris.Application.Policy.TableAccessPolicy";
    private const string TypedPolicySourcesName = "Autheris.Application.Policy.TypedPolicySources";

    private static List<TypeDefinition> TypesCreating(string typeName)
    {
        var found = new List<TypeDefinition>();
        foreach (var path in Assemblies.Distinct())
        {
            var module = CecilModuleCache.For(path);
            foreach (var type in module.GetTypes().Where(t => t.FullName != typeName))
            {
                bool creates = type.Methods.Where(m => m.HasBody).Any(m => m.Body.Instructions.Any(i =>
                    i.OpCode.Code == Code.Newobj && i.Operand is MethodReference ctor && ctor.DeclaringType.FullName == typeName));
                if (creates)
                {
                    found.Add(type);
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> ReferencedTypes(TypeDefinition type) =>
        type.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand)
            .SelectMany(o => o switch
            {
                MethodReference method => new[] { method.DeclaringType.FullName, method.ReturnType.FullName },
                TypeReference reference => new[] { reference.FullName },
                FieldReference field => new[] { field.DeclaringType.FullName, field.FieldType.FullName },
                _ => []
            });

    // Positive control: without it a rename of the scanned constructor would make the site scan pass vacuously.
    [Fact]
    public void TheScanner_FindsTheKnownDecisionSites()
    {
        var sites = TypesCreating(TableAccessPolicyName).Select(t => t.FullName.Split('/')[0]).Distinct().ToList();

        sites.ShouldContain("Autheris.Application.Policy.UnifiedPolicyDecisionPoint");
        sites.ShouldContain("Autheris.Application.Services.GatewayExecutionService");
        sites.ShouldContain("Autheris.Application.Sql.Services.GovernedSqlRewriter");
        sites.ShouldContain("Autheris.Application.Sql.Services.FederatedDuckDbExecutionService");
        sites.ShouldContain("Autheris.Application.Procedures.Services.GovernedProcedureExecutionService");
        sites.ShouldContain("Autheris.Application.Connectors.CrossDomain.DefaultCrossDomainAccessResolver");
    }

    [Fact]
    public void EveryDecisionSite_HoldsTheTypedPolicySources()
    {
        var missing = TypesCreating(TableAccessPolicyName)
            .Select(t => t.DeclaringType ?? t)
            .Distinct()
            .Where(t => !t.Fields.Any(f => !f.IsStatic && f.FieldType.FullName == TypedPolicySourcesName))
            .Select(t => t.FullName)
            .ToList();

        missing.ShouldBeEmpty("These types create a TableAccessPolicy without the typed policy sources: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("Autheris.Application.Sql.Policy.GovernancePolicyFactory")]
    [InlineData("Autheris.Application.Sql.Policy.CompileRequestFactory")]
    public void PolicyAssembly_NeverUsesTheStringPolicyProvidersOrTheRlsOptionsBag(string typeName)
    {
        var module = CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);
        var types = module.GetTypes().Where(t => t.FullName == typeName || (t.DeclaringType?.FullName == typeName)).ToList();
        types.ShouldNotBeEmpty();

        var forbidden = new[]
        {
            "TrinoSqlEngine.RlsOptions", "TrinoSqlEngine.DefaultRlsPolicyProvider", "TrinoSqlEngine.DefaultColumnMaskingPolicyProvider",
            "TrinoSqlEngine.IRlsPolicyProvider", "TrinoSqlEngine.IColumnMaskingPolicyProvider"
        };
        var used = types.SelectMany(ReferencedTypes).Where(forbidden.Contains).Distinct().ToList();

        used.ShouldBeEmpty(string.Join(", ", used));
    }
}
