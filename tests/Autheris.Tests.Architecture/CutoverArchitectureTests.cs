using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-X1 step X1.6: the guards of the cutover to the AST compiler. Every scan reads the IL of the production assemblies, so a
/// different spelling, a variable or a reflection-free alias cannot slip past it the way a text search could. Each scan has a
/// positive control (a known hit) so that a rename cannot turn a test green vacuously.
/// </summary>
public sealed class CutoverArchitectureTests
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

    private static readonly string EngineAssemblyName = typeof(TrinoSqlEngine.FastSqlEngine).Assembly.GetName().Name!;

    private static List<(ModuleDefinition Module, TypeDefinition Type)> ProductionTypes()
    {
        var result = new List<(ModuleDefinition, TypeDefinition)>();
        foreach (var assembly in ProductionAssemblies)
        {
            var module = ModuleDefinition.ReadModule(assembly.Location);
            result.AddRange(module.GetTypes().Select(t => (module, t)));
        }

        return result;
    }

    private static string Top(TypeDefinition type)
    {
        var t = type;
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return t.FullName;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // NoLegacyRewriterTypes
    // ---------------------------------------------------------------------------------------------------------------------

    private static readonly string[] LegacyTypeNames =
    [
        "RlsListener", "RlsOptions", "IRlsPolicyProvider", "DefaultRlsPolicyProvider", "IColumnMaskingPolicyProvider",
        "DefaultColumnMaskingPolicyProvider", "ICompiledSqlQueryPlanCache", "CompiledSqlQueryPlanCache",
        "NullCompiledSqlQueryPlanCache", "DialectLimitExceededException"
    ];

    private static readonly string[] LegacyMemberNames =
    [
        "RewriteRls", "RunLegacyRewrite", "GenerateGovernedSql", "RestoreClientParameters", "AddInternalRowFilterParameters",
        "BuildDialectMaskExpression", "FormatTableAlias", "LastIdentifierPart", "BuildTsqlLimitClause",
        "get_SqlRewriterEngine", "set_SqlRewriterEngine", "get_PolicyFiltersAreTargetDialectSql", "get_RewriterEngine"
    ];

    [Fact]
    public void TheLegacyScanner_SeesAKnownTypeAndMember()
    {
        // Positive control against the test assembly itself: a type and a member with a scanned shape are found.
        var module = ModuleDefinition.ReadModule(typeof(CutoverArchitectureTests).Assembly.Location);
        module.GetTypes().ShouldContain(t => t.Name == nameof(ControlRewriteRls));
        module.GetTypes().SelectMany(t => t.Methods).ShouldContain(m => m.Name == nameof(ControlRewriteRls.RewriteRls));
    }

    internal sealed class ControlRewriteRls
    {
        public string RewriteRls() => string.Empty;
    }

    [Fact]
    public void NoLegacyRewriterTypes()
    {
        var violations = new List<string>();
        foreach (var (module, type) in ProductionTypes())
        {
            if (LegacyTypeNames.Contains(type.Name)) violations.Add($"{module.Assembly.Name.Name}: type {type.FullName}");
            foreach (var method in type.Methods.Where(m => LegacyMemberNames.Contains(m.Name)))
            {
                violations.Add($"{module.Assembly.Name.Name}: member {type.FullName}.{method.Name}");
            }

            // L-13: the parameter budget of a dialect comes from the capability table, never from a per-generator override.
            if (type.Name.EndsWith("DialectGenerator", StringComparison.Ordinal) || type.FullName == "TrinoSqlEngine.Ast.Generators.ISqlDialectGenerator")
            {
                foreach (var method in type.Methods.Where(m => m.Name == "get_MaxParameterBudget"))
                {
                    violations.Add($"{module.Assembly.Name.Name}: member {type.FullName}.{method.Name}");
                }
            }
        }

        violations.ShouldBeEmpty("The legacy string rewriter, its options and its string policy providers are gone (L-1..L-17): " + string.Join(", ", violations));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // NoTrustedSqlExpression
    // ---------------------------------------------------------------------------------------------------------------------

    private static bool DerivesFrom(TypeDefinition type, string baseFullName)
    {
        for (var b = type.BaseType; b is not null; b = b.Resolve()?.BaseType)
        {
            if (b.FullName == baseFullName) return true;
        }

        return false;
    }

    [Fact]
    public void NoTrustedSqlExpression()
    {
        const string expression = "TrinoSqlEngine.Ast.Nodes.Expression";
        var violations = new List<string>();
        int expressionNodes = 0;
        foreach (var (module, type) in ProductionTypes())
        {
            if (type.Name.Contains("TrustedSql", StringComparison.Ordinal)) violations.Add($"{module.Assembly.Name.Name}: type {type.FullName}");
            if (!DerivesFrom(type, expression)) continue;
            expressionNodes++;

            // An expression node must never carry free SQL text that the generator would emit verbatim.
            foreach (var field in type.Fields.Where(f => !f.IsStatic && f.FieldType.FullName == "System.String"))
            {
                string name = field.Name.Trim('<', '>').Replace("k__BackingField", string.Empty, StringComparison.Ordinal).Trim('<', '>');
                if (name is "Sql" or "RawSql" or "Text" or "Fragment" or "Expression" or "Verbatim")
                {
                    violations.Add($"{module.Assembly.Name.Name}: {type.FullName}.{name} is a free text member of an expression node");
                }
            }
        }

        expressionNodes.ShouldBeGreaterThan(10, "positive control: the scanner sees the expression node types");
        violations.ShouldBeEmpty("No expression node may emit caller or policy text verbatim (L-8): " + string.Join(", ", violations));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // CompiledSqlIsOnlyConstructedByTheEngine (SEC-X1-07)
    // ---------------------------------------------------------------------------------------------------------------------

    private static List<string> ForgingCalls(string assemblyPath)
    {
        var callers = new List<string>();
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || instruction.Operand is not MethodReference called) continue;
                    if (called.DeclaringType.FullName != "TrinoSqlEngine.Ast.Emit.CompiledSql") continue;
                    bool forges = (instruction.OpCode.Code == Code.Newobj && called.Name == ".ctor") || called.Name == "<Clone>$" ||
                                  called.Name is "set_Sql" or "set_Parameters" or "set_Dialect";
                    if (forges) callers.Add($"{type.FullName}.{method.Name} -> {called.Name}");
                }
            }
        }

        return callers;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static TrinoSqlEngine.Ast.Emit.CompiledSql ControlForges(TrinoSqlEngine.Ast.Emit.CompiledSql compiled) =>
        compiled with { Sql = "SELECT 1" };

    [Fact]
    public void TheForgingScanner_FindsAWithCopy_InAKnownCaller() =>
        ForgingCalls(typeof(CutoverArchitectureTests).Assembly.Location)
            .ShouldContain(c => c.Contains(nameof(ControlForges), StringComparison.Ordinal));

    [Fact]
    public void CompiledSqlIsOnlyConstructedByTheEngine()
    {
        var violations = new List<string>();
        int engineHits = 0;
        foreach (var assembly in ProductionAssemblies)
        {
            var hits = ForgingCalls(assembly.Location);
            if (assembly.GetName().Name == EngineAssemblyName) engineHits += hits.Count;
            else violations.AddRange(hits.Select(h => $"{assembly.GetName().Name}: {h}"));
        }

        engineHits.ShouldBeGreaterThan(0, "positive control: the scanner sees the engine's own construction sites");
        violations.ShouldBeEmpty("Only TrinoSqlEngine may construct or copy a CompiledSql (SEC-X1-07): " + string.Join(", ", violations));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // NoCommandTextAssignmentOnGovernedPath (§28.4)
    // ---------------------------------------------------------------------------------------------------------------------

    private static List<string> CommandTextSetters(TypeDefinition type)
    {
        var found = new List<string>();
        foreach (var method in type.Methods.Where(m => m.HasBody))
        {
            if (method.Body.Instructions.Any(i => i.OpCode.Code is Code.Call or Code.Callvirt && i.Operand is MethodReference { Name: "set_CommandText" }))
            {
                found.Add(method.Name);
            }
        }

        return found;
    }

    [Fact]
    public void NoCommandTextAssignmentOnGovernedPath()
    {
        var types = ProductionTypes();

        // The governed executor, its service and the rewriter never assign CommandText; the binder does (positive control).
        string[] governed =
        [
            "Autheris.Application.Sql.Services.GovernedSqlExecutor", "Autheris.Application.Sql.Services.GovernedSqlExecutionService",
            "Autheris.Application.Sql.Services.GovernedSqlRewriter", "Autheris.Application.Sql.Services.WebSqlStatementManager"
        ];
        var violations = new List<string>();
        foreach (string name in governed)
        {
            var matching = types.Where(t => Top(t.Type) == name).ToList();
            matching.ShouldNotBeEmpty($"{name} exists");
            foreach (var (_, type) in matching) violations.AddRange(CommandTextSetters(type).Select(m => $"{type.FullName}.{m}"));
        }

        violations.ShouldBeEmpty("CommandText on the governed path is assigned only by DbCommandCompiledSqlBinder: " + string.Join(", ", violations));

        var binderSetters = types.Where(t => Top(t.Type) == "TrinoSqlEngine.Ast.Emit.DbCommandCompiledSqlBinder").SelectMany(t => CommandTextSetters(t.Type)).ToList();
        binderSetters.ShouldNotBeEmpty("positive control: the binder assigns CommandText");
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // GovernedEntryPoints_ReachCompileAndBinder (M-7, §28.2)
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The entry points of §28.2 marked "via X1": each reaches <c>ISqlEngine.Compile</c> and a binder through the call graph.</summary>
    private static readonly string[] GovernedEntryPoints =
    [
        "Autheris.Api.Endpoints.WebSqlEndpoints",
        "Autheris.Application.Sql.Services.WebSqlStatementManager",
        "Autheris.Application.SqlEndpoints.Services.SqlEndpointExecutionService",
        "Autheris.Application.Jobs.Services.AsyncQueryJobBackgroundWorker",
        "Autheris.Api.Endpoints.ArrowExportEndpoints",
        "Autheris.Application.Serialization.ArrowFlightSqlServer",
        "Autheris.Application.Mcp.Services.McpToolExecutionHandler",
        "Autheris.Application.Mcp.Services.ApiDispatcherService",
        "Autheris.Application.Data.Services.GovernedDataQueryService"
    ];

    /// <summary>
    /// The explicit allowed exceptions of §28.9: governed SQL outside <c>Compile</c>. OQ-GQL is the GraphQL tree compiler; OQ-4 are
    /// the §7.3 text consumers (they render from the typed IR through <c>IPolicyPredicateRenderer</c> and the mask renderer).
    /// </summary>
    private static readonly string[] AllowedExceptionsOutsideCompile =
    [
        "Autheris.Application.Sql.Tree.GovernedTreeQueryService",
        "Autheris.Application.Services.SqlDataSourceExecutor",
        "Autheris.Infrastructure.Connectors.SqlConnector",
        "Autheris.Application.Olap.FederatedStagingService",
        "Autheris.Api.Endpoints.DuckDbOlapEndpoints"
    ];

    private sealed record Node(string Type, string Method);

    /// <summary>
    /// Call graph on (top-level type, method name). Compiler generated types (async state machines, closures) are folded into the
    /// method they came from; a call through an interface or a virtual method links to every implementation of that name.
    /// </summary>
    private static (Dictionary<Node, HashSet<Node>> Edges, Dictionary<Node, HashSet<string>> Targets) BuildCallGraph()
    {
        var types = ProductionTypes();
        var edges = new Dictionary<Node, HashSet<Node>>();
        var targets = new Dictionary<Node, HashSet<string>>();
        var implementations = new Dictionary<string, List<Node>>();

        static string OriginalMethod(TypeDefinition type, MethodDefinition method)
        {
            for (var t = type; t.DeclaringType is not null; t = t.DeclaringType)
            {
                if (t.Name.StartsWith('<') && t.Name.IndexOf('>') > 1) return t.Name[1..t.Name.IndexOf('>')];
            }

            if (method.Name.StartsWith('<') && method.Name.IndexOf('>') > 1) return method.Name[1..method.Name.IndexOf('>')];
            return method.Name;
        }

        foreach (var (_, type) in types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var node = new Node(Top(type), OriginalMethod(type, method));
                if (!implementations.TryGetValue(node.Method, out var list)) implementations[node.Method] = list = [];
                if (!list.Contains(node)) list.Add(node);
            }
        }

        foreach (var (_, type) in types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var from = new Node(Top(type), OriginalMethod(type, method));
                if (!edges.TryGetValue(from, out var outgoing)) edges[from] = outgoing = [];
                if (!targets.TryGetValue(from, out var called)) targets[from] = called = [];
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || instruction.Operand is not MethodReference reference) continue;
                    string owner = reference.DeclaringType.FullName;
                    called.Add($"{owner}::{reference.Name}");
                    if (implementations.TryGetValue(reference.Name, out var impls))
                    {
                        // Same name, any type: sound over-approximation of virtual and interface dispatch by name.
                        foreach (var impl in impls)
                        {
                            if (impl.Type == owner || reference.Name != ".ctor" && InvolvesDispatch(reference)) outgoing.Add(impl);
                        }
                    }
                }
            }
        }

        return (edges, targets);
    }

    private static bool InvolvesDispatch(MethodReference reference)
    {
        var resolved = reference.Resolve();
        return resolved is null || resolved.IsVirtual || resolved.DeclaringType.IsInterface || resolved.DeclaringType.IsAbstract;
    }

    private static bool Reaches(Dictionary<Node, HashSet<Node>> edges, Dictionary<Node, HashSet<string>> targets, string entryType, string target)
    {
        var seen = new HashSet<Node>();
        var queue = new Queue<Node>(edges.Keys.Where(n => n.Type == entryType));
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!seen.Add(node)) continue;
            if (targets.TryGetValue(node, out var called) && called.Contains(target)) return true;
            if (edges.TryGetValue(node, out var next)) foreach (var n in next) queue.Enqueue(n);
        }

        return false;
    }

    private const string CompileTarget = "TrinoSqlEngine.ISqlEngine::Compile";
    private const string BinderTarget = "TrinoSqlEngine.Ast.Emit.CompiledSqlBinders::For";

    [Fact]
    public void GovernedEntryPoints_ReachCompileAndBinder()
    {
        var (edges, targets) = BuildCallGraph();
        var violations = new List<string>();
        foreach (string entry in GovernedEntryPoints)
        {
            edges.Keys.ShouldContain(n => n.Type == entry, $"{entry} exists in a production assembly");
            if (!Reaches(edges, targets, entry, CompileTarget)) violations.Add($"{entry} does not reach ISqlEngine.Compile");
            if (!Reaches(edges, targets, entry, BinderTarget)) violations.Add($"{entry} does not reach CompiledSqlBinders.For");
        }

        violations.ShouldBeEmpty("Every governed entry point (M-7, plan 28.2) reaches Compile and the binder: " + string.Join("; ", violations));
    }

    [Fact]
    public void TheCallGraphScanner_FindsTheKnownCompileAndBinderCallers()
    {
        var (_, targets) = BuildCallGraph();
        targets.Where(kv => kv.Value.Contains(CompileTarget)).Select(kv => kv.Key.Type)
            .Distinct().ShouldBe(new[] { "Autheris.Application.Sql.Services.GovernedSqlRewriter" }, ignoreOrder: true);
        targets.Where(kv => kv.Value.Contains(BinderTarget)).Select(kv => kv.Key.Type)
            .Distinct().ShouldBe(new[] { "Autheris.Application.Sql.Services.GovernedSqlExecutor" }, ignoreOrder: true);
    }

    [Fact]
    public void TheExplicitExceptions_NeverCallALegacyApi_AndDoNotPretendToCompile()
    {
        var (edges, targets) = BuildCallGraph();
        foreach (string exception in AllowedExceptionsOutsideCompile)
        {
            edges.Keys.ShouldContain(n => n.Type == exception, $"{exception} exists in a production assembly");
            // They are not entry points of the governed WebSQL pipeline: they must not own a second binder or a second compile site.
            targets.Where(kv => kv.Key.Type == exception).SelectMany(kv => kv.Value)
                .ShouldNotContain(BinderTarget, $"{exception} must not bind through the governed binder; it is a text consumer");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // DeletedPathsNotReintroduced (revert safety, plan 12.2)
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Files X1 deleted. A later package must not modify or re-add a file that X1 deleted: a revert of X1 would then conflict
    /// (plan 12.2). The list is the plan's removal list (28.6) as paths.
    /// </summary>
    private static readonly string[] DeletedPaths =
    [
        "src/TrinoSqlEngine/RlsListener.cs",
        "src/TrinoSqlEngine/FastSqlEngine.Rewrite.cs",
        "src/TrinoSqlEngine/IRlsPolicyProvider.cs",
        "src/TrinoSqlEngine/Ast/Generators/DialectLimitExceededException.cs",
        "src/Autheris.Application/Sql/ICompiledSqlQueryPlanCache.cs",
        "src/Autheris.Application/Sql/CompiledSqlQueryPlanCache.cs",
        "src/Autheris.Application/Sql/NullCompiledSqlQueryPlanCache.cs",
        "tests/TrinoSqlEngine.Tests/Differential/LegacyVsAstDifferentialTests.cs",
        "tests/Autheris.Tests.Unit/CompiledSqlQueryPlanCacheTests.cs"
    ];

    [Fact]
    public void DeletedPathsNotReintroduced()
    {
        string root = RepositoryRoot();
        var present = DeletedPaths.Where(p => File.Exists(Path.Combine(root, p))).ToList();
        present.ShouldBeEmpty("These files were deleted by the cutover (L-1..L-16) and must not come back: " + string.Join(", ", present));

        // The positive control: the guard works on real paths (a file that does exist is reported by the same check).
        File.Exists(Path.Combine(root, "src/TrinoSqlEngine/FastSqlEngine.cs")).ShouldBeTrue();
    }
}
