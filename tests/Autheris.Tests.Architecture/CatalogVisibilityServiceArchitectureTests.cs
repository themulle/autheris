using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// #138 INC-1 (AC-138-2, D-6, SEC-CAT138-11, T-CAT138-14): the catalog visibility engine is reached by discovery code only through
/// <c>ICatalogVisibilityService</c>. Calls, method references (delegates, <c>ldftn</c>) and compiler-generated closure types are
/// attributed to their declaring root type. This test complements the source guards in <c>CatalogSurfaceCallSiteGuardTests</c>.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogVisibilityServiceArchitectureTests
{
    private const string Engine = "Autheris.Application.Services.CatalogVisibility";
    private const string McpEngine = "Autheris.Application.Mcp.Services.McpCatalogVisibility";
    private static readonly string[] EngineMethods = ["VisibleTablesAsync", "FilterForSubject", "FilterToTenant"];

    /// <summary>The service, the engine itself, and the DataAccess consumers (S12, plan OQ-3: consent-only by design).</summary>
    private static readonly string[] Allowed =
    [
        Engine,
        McpEngine,
        "Autheris.Application.Catalog.Services.CatalogVisibilityService",
        "Autheris.Application.Governance.Services.EffectiveAccessExplanationService",
        "Autheris.Application.Mcp.Services.AiDataGuardrailService",
        "Autheris.Application.Mcp.Services.SemanticMcpCompiler",
        "Autheris.Application.Serialization.ArrowFlightSqlServer",
        "Autheris.Application.Sql.Services.TrinoMetadataQueryHandler",
        "Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService"
    ];

    private static readonly string[] DiscoveryRoots =
    [
        "Autheris.Application.Catalog.Services.CatalogDiscoveryService",
        "Autheris.Application.Mcp.Services.McpDatasetCatalog",
        "Autheris.GraphQL.Types.Query"
    ];

    private static IEnumerable<ModuleDefinition> ProductionModules()
    {
        yield return CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.GraphQL.Types.Mutation).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Api.Security.GatewayAuthSchemes).Assembly);
        yield return CecilModuleCache.For(typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly);
    }

    /// <summary>The declaring root type: <c>A.B/&lt;&gt;c__DisplayClass0_0</c> and <c>A.B/&lt;Run&gt;d__3</c> belong to <c>A.B</c>.</summary>
    private static string Root(TypeDefinition type)
    {
        while (type.DeclaringType != null)
        {
            type = type.DeclaringType;
        }

        return type.FullName;
    }

    private static IEnumerable<MethodReference> MethodRefs(TypeDefinition type) =>
        type.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand)
            .OfType<MethodReference>();

    private static bool IsEngineMethod(MethodReference m)
    {
        var declaring = m.DeclaringType.FullName;
        return (declaring == Engine || declaring == McpEngine) && EngineMethods.Contains(m.Name);
    }

    internal static List<string> EngineCallers(IEnumerable<ModuleDefinition> modules, IReadOnlyCollection<string> allowed)
    {
        var violations = new List<string>();
        foreach (var module in modules)
        {
            foreach (var type in module.GetTypes())
            {
                var root = Root(type);
                if (allowed.Contains(root))
                {
                    continue;
                }

                foreach (var callee in MethodRefs(type).Where(IsEngineMethod))
                {
                    violations.Add($"{root} -> {callee.DeclaringType.Name}.{callee.Name}");
                }
            }
        }

        return violations.Distinct().ToList();
    }

    [Fact]
    public void OnlyServiceAndDataAccessAllowList_CallCatalogVisibility()
    {
        var violations = EngineCallers(ProductionModules(), Allowed);

        violations.ShouldBeEmpty("Discovery visibility goes through ICatalogVisibilityService: " + string.Join("; ", violations));
    }

    [Fact]
    public void Architecture_ClosureCallersDetected_ByDeclaringRootType()
    {
        // The scanner must flag a lambda (compiler-generated closure type) in this test assembly that calls the engine.
        var self = CecilModuleCache.For(typeof(CatalogVisibilityServiceArchitectureTests).Assembly);

        var violations = EngineCallers([self], []);

        violations.ShouldContain(v => v.StartsWith(typeof(SyntheticCatalogEngineCaller).FullName!, StringComparison.Ordinal));
        self.GetTypes().Any(t => t.DeclaringType?.FullName == typeof(SyntheticCatalogEngineCaller).FullName && t.Name.Contains("DisplayClass", StringComparison.Ordinal))
            .ShouldBeTrue("the synthetic offender must compile into a closure type, otherwise this test proves nothing");
    }

    [Fact]
    public void DiscoveryCode_HasNoRebacEvaluatorOrTableGateReference()
    {
        var module = CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);
        var graphQl = CecilModuleCache.For(typeof(Autheris.GraphQL.Types.Mutation).Assembly);
        var violations = new List<string>();
        foreach (var type in module.GetTypes().Concat(graphQl.GetTypes()).Where(t => DiscoveryRoots.Contains(Root(t))))
        {
            foreach (var reference in References(type))
            {
                if (reference.Contains("IRebacEvaluator", StringComparison.Ordinal) || reference.Contains("RebacTableGate", StringComparison.Ordinal))
                {
                    violations.Add($"{Root(type)} -> {reference}");
                }
            }
        }

        violations.Distinct().ShouldBeEmpty("Discovery code must not evaluate ReBAC itself: " + string.Join("; ", violations));
    }

    private static IEnumerable<string> References(TypeDefinition type)
    {
        foreach (var field in type.Fields)
        {
            yield return field.FieldType.FullName;
        }

        foreach (var method in type.Methods)
        {
            yield return method.ReturnType.FullName;
            foreach (var p in method.Parameters)
            {
                yield return p.ParameterType.FullName;
            }

            if (!method.HasBody)
            {
                continue;
            }

            foreach (var v in method.Body.Variables)
            {
                yield return v.VariableType.FullName;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference m:
                        yield return m.DeclaringType.FullName;
                        yield return m.ReturnType.FullName;
                        foreach (var p in m.Parameters)
                        {
                            yield return p.ParameterType.FullName;
                        }

                        break;
                    case FieldReference f:
                        yield return f.DeclaringType.FullName;
                        yield return f.FieldType.FullName;
                        break;
                    case TypeReference t:
                        yield return t.FullName;
                        break;
                }
            }
        }
    }

    [Fact]
    public void EngineRowReturningMethods_AreTheGuardedSetPlusTheNarrowingOnlyContractFilter()
    {
        // A new public method that returns catalog rows must be added to the guarded set (or consciously allowed here), otherwise a new
        // visibility entry point could bypass ICatalogVisibilityService unnoticed. FilterByContract only narrows by a schema contract
        // (no consent or tier decision), so it is allowed everywhere; ComputeAccessStatus returns a status string, not rows.
        string[] narrowingOnly = ["FilterByContract"];
        var module = CecilModuleCache.For(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly);
        var engine = module.GetType(Engine);

        var rowReturning = engine.Methods
            .Where(m => m.IsPublic && m.ReturnType.FullName.Contains("TableMetadata", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Distinct()
            .Order()
            .ToArray();

        rowReturning.ShouldBe([.. EngineMethods.Concat(narrowingOnly).Order()]);
    }

    [Fact]
    public void RemovedMembersAbsent_ResolvePermittedAsync_IsRebacAllowedAsync()
    {
        var removed = new[] { "ResolvePermittedAsync", "IsRebacAllowedAsync" };
        var found = new List<string>();
        foreach (var module in ProductionModules())
        {
            foreach (var type in module.GetTypes())
            {
                found.AddRange(type.Methods.Where(m => removed.Contains(m.Name)).Select(m => $"{type.FullName}::{m.Name}"));
            }
        }

        found.ShouldBeEmpty("SEC-CAT138-11: the divergent discovery gates are gone: " + string.Join("; ", found));
    }
}

/// <summary>Synthetic offender (test-only): a capturing lambda that calls the engine, compiled into a closure type nested in this class.</summary>
internal static class SyntheticCatalogEngineCaller
{
    public static Func<IReadOnlyList<Autheris.Domain.Model.TableMetadata>> Build(IReadOnlyList<Autheris.Domain.Model.TableMetadata> all, Autheris.Domain.Common.TenantId tenant)
    {
        return () => Autheris.Application.Services.CatalogVisibility.FilterToTenant(all, tenant, [], null);
    }
}
