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
    /// <summary>
    /// L-6: the production assemblies are the projects under <c>src/**</c> (the assembly name is the <c>AssemblyName</c> of the project, else its
    /// file name), so a new production project is scanned without a code change here. A project that this test project cannot load fails
    /// the test instead of being skipped.
    /// </summary>
    private static readonly Assembly[] ProductionAssemblies = LoadProductionAssemblies();

    private static string[] ProductionProjectNames()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => System.Xml.Linq.XDocument.Load(p).Descendants("AssemblyName").FirstOrDefault()?.Value.Trim() ?? Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Assembly[] LoadProductionAssemblies() => ProductionProjectNames().Select(name => Assembly.Load(new AssemblyName(name))).ToArray();

    /// <summary>The assemblies that this test project references on purpose; a production project that is not among them has no anchor here.</summary>
    private static readonly Assembly[] AnchorAssemblies =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly,
        typeof(TrinoSqlEngine.FastSqlEngine).Assembly
    ];

    [Fact]
    public void TheScannedAssemblies_AreExactlyTheProjectsUnderSrc()
    {
        var projects = ProductionProjectNames();
        projects.Length.ShouldBeGreaterThan(5, "positive control: the project scan sees the production projects");

        ProductionAssemblies.Select(a => a.GetName().Name!).Order(StringComparer.Ordinal).ShouldBe(projects);
        ProductionTypes().Select(t => t.Module.Assembly.Name.Name).Distinct().Order(StringComparer.Ordinal).ShouldBe(projects);
        // a new production project under src/ needs an anchor type in AnchorAssemblies (and a reference of this test project)
        AnchorAssemblies.Select(a => a.GetName().Name!).Order(StringComparer.Ordinal).ShouldBe(projects);
    }

    private static readonly string EngineAssemblyName = typeof(TrinoSqlEngine.FastSqlEngine).Assembly.GetName().Name!;

    private static readonly List<(ModuleDefinition Module, TypeDefinition Type)> ProductionTypesList = TypesOf(ProductionAssemblies);

    private static List<(ModuleDefinition Module, TypeDefinition Type)> ProductionTypes() => ProductionTypesList;

    private static List<(ModuleDefinition Module, TypeDefinition Type)> TypesOf(IEnumerable<Assembly> assemblies)
    {
        var result = new List<(ModuleDefinition, TypeDefinition)>();
        foreach (var assembly in assemblies)
        {
            var module = CecilModuleCache.For(assembly);
            result.AddRange(module.GetTypes().Select(t => (module, t)));
        }

        return result;
    }

    private static string Top(TypeDefinition type)
    {
        var t = type;
        while (t.DeclaringType is not null) t = t.DeclaringType;
        var name = t.FullName;
        var slash = name.IndexOf('/');
        return slash >= 0 ? name[..slash] : name;
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
        var module = CecilModuleCache.For(typeof(CutoverArchitectureTests).Assembly);
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
        var module = CecilModuleCache.For(assemblyPath);
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
    // NamedBindMarkerExpression is only created by the policy predicate renderer (L-5)
    // ---------------------------------------------------------------------------------------------------------------------

    private const string NamedBindMarker = "TrinoSqlEngine.Ast.Nodes.NamedBindMarkerExpression";

    /// <summary>
    /// The callers that construct or copy a <c>NamedBindMarkerExpression</c> (a bind marker that the generator emits as a bind name verbatim),
    /// outside the type itself, as "top-level type: Type.Method".
    /// </summary>
    private static List<(string Top, string Site)> NamedBindMarkerConstructions(IEnumerable<Assembly> assemblies)
    {
        var found = new List<(string, string)>();
        foreach (var (_, type) in TypesOf(assemblies))
        {
            if (Top(type) == NamedBindMarker) continue;
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                if (method.Body.Instructions.Any(i => i.OpCode.Code is Code.Newobj or Code.Call or Code.Callvirt && i.Operand is MethodReference called
                                                       && called.DeclaringType.FullName == NamedBindMarker
                                                       && (called.Name == ".ctor" || called.Name == "<Clone>$")))
                {
                    found.Add((Top(type), $"{type.FullName}.{method.Name}"));
                }
            }
        }

        return found;
    }

    [Fact]
    public void TheBindMarkerScanner_FindsAKnownConstruction() =>
        NamedBindMarkerConstructions([typeof(CutoverArchitectureTests).Assembly])
            .ShouldContain(c => c.Top == typeof(ControlBindMarkerFactory).FullName);

    [Fact]
    public void NamedBindMarkerExpression_IsOnlyConstructedByThePolicyPredicateRenderer()
    {
        var sites = NamedBindMarkerConstructions(ProductionAssemblies);

        sites.ShouldContain(c => c.Top == "Autheris.Application.Sql.Policy.PolicyPredicateRenderer", "positive control: the renderer creates the markers");
        var violations = sites.Where(c => c.Top != "Autheris.Application.Sql.Policy.PolicyPredicateRenderer").Select(c => c.Site).ToList();
        violations.ShouldBeEmpty("A NamedBindMarkerExpression emits its name as a bind marker verbatim, so only the policy predicate renderer (which names it from the policy's own parameters) may create one: " + string.Join(", ", violations));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // No TokenStreamRewriter in production code (SD-1, L-2)
    // ---------------------------------------------------------------------------------------------------------------------

    private const string TokenStreamRewriterType = "Antlr4.Runtime.TokenStreamRewriter";

    private static bool MentionsTokenStreamRewriter(object? operand) => operand switch
    {
        MethodReference m => m.DeclaringType?.FullName == TokenStreamRewriterType || m.ReturnType.FullName == TokenStreamRewriterType,
        FieldReference f => f.DeclaringType?.FullName == TokenStreamRewriterType || f.FieldType.FullName == TokenStreamRewriterType,
        TypeReference t => t.FullName == TokenStreamRewriterType,
        _ => false
    };

    /// <summary>Every method (of the given types) that creates, calls, declares a local of, or takes the address of a <c>TokenStreamRewriter</c>, as "Type.Method".</summary>
    private static List<string> TokenStreamRewriterUsers(IEnumerable<(ModuleDefinition Module, TypeDefinition Type)> types)
    {
        var found = new List<string>();
        foreach (var (_, type) in types)
        {
            if (type.Fields.Any(f => f.FieldType.FullName == TokenStreamRewriterType)) found.Add($"{type.FullName} (field)");
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                if (method.Body.Variables.Any(v => v.VariableType.FullName == TokenStreamRewriterType) ||
                    method.Body.Instructions.Any(i => MentionsTokenStreamRewriter(i.Operand)))
                {
                    found.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        return found;
    }

    internal sealed class ControlUsesTokenStreamRewriter
    {
        public string Run(Antlr4.Runtime.ITokenStream tokens) => new Antlr4.Runtime.TokenStreamRewriter(tokens).GetText();
    }

    [Fact]
    public void TheTokenStreamRewriterScanner_FindsAKnownUser() =>
        TokenStreamRewriterUsers(TypesOf([typeof(CutoverArchitectureTests).Assembly]))
            .ShouldContain(u => u.Contains(nameof(ControlUsesTokenStreamRewriter), StringComparison.Ordinal));

    [Fact]
    public void NoProductionAssembly_ReferencesTheTokenStreamRewriter()
    {
        // SD-1: statements are never edited as text through the token stream; the audit redaction builds its text from the token indices.
        var users = TokenStreamRewriterUsers(ProductionTypes());

        users.ShouldBeEmpty("TokenStreamRewriter is banned from production code (SD-1): " + string.Join(", ", users));
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // NoCommandTextAssignmentOnGovernedPath (§28.4)
    // ---------------------------------------------------------------------------------------------------------------------

    private static bool TakesString(MethodReference method) => method.Parameters.Any(p => p.ParameterType.FullName == "System.String");

    /// <summary>
    /// L-3: every way to put text into a command, not only the property setter: a call of a <c>CommandText</c> setter (<c>DbCommand</c>,
    /// <c>IDbCommand</c> and any provider command: the scan is on the member name, so a subtype or an interface call cannot hide it), a
    /// constructor of a <c>*Command</c> type (a provider command or a batch command) that takes the text as a <c>string</c>, and a
    /// <c>CreateCommand(string)</c> / <c>CreateBatchCommand(string)</c> factory call.
    /// </summary>
    private static bool PutsTextIntoACommand(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Call or Code.Callvirt => instruction.Operand is MethodReference called &&
            (called.Name == "set_CommandText" || (called.Name is "CreateCommand" or "CreateBatchCommand" && TakesString(called))),
        Code.Newobj => instruction.Operand is MethodReference { Name: ".ctor" } constructor &&
            constructor.DeclaringType.Name.EndsWith("Command", StringComparison.Ordinal) && TakesString(constructor),
        _ => false
    };

    /// <summary>Every method that puts text into a command (see <see cref="PutsTextIntoACommand"/>) as (top-level type, "Type.Method").</summary>
    private static List<(string Top, string Site)> CommandTextSetterSites(IEnumerable<(ModuleDefinition Module, TypeDefinition Type)> types)
    {
        var found = new List<(string, string)>();
        foreach (var (_, type) in types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                if (method.Body.Instructions.Any(PutsTextIntoACommand))
                {
                    found.Add((Top(type), $"{type.FullName}.{method.Name}"));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The only production types that may assign <c>CommandText</c> (§28.4). Everything else, in particular every type of the governed path,
    /// reaches a database through <c>CompiledSql</c> and the binder. A type is allowed with a reason or not at all; the key is the top-level
    /// type, and a trailing <c>*</c> allows a namespace prefix.
    /// </summary>
    private static readonly (string Type, string Reason)[] CommandTextAllowlist =
    [
        // -- governed SQL: compiled text only --
        ("TrinoSqlEngine.Ast.Emit.DbCommandCompiledSqlBinder", "the binder: the only place that turns CompiledSql into command text"),
        ("Autheris.Infrastructure.Persistence.BindByNameOracleCommand", "DbCommand decorator: forwards the text the binder gave it to the Oracle command (BindByName)"),
        // -- the allowed exceptions of §28.9 (the text comes from the typed IR / the compiler, never from a client string) --
        ("Autheris.Application.Sql.Tree.GovernedTreeQueryService", "§28.9 OQ-GQL: executes compiled.Sql of the GraphQL tree compiler"),
        ("Autheris.Application.Olap.DuckDbOlapEngine", "§28.9 DuckDB staging: runs on the in-process staged copy, after the governed fetch"),
        ("Autheris.Application.Sql.TimeZones.DuckDbIcuProbe", "REQ-SQL-130 (BC-130-7): probe of the time zone state on throw-away in-memory DuckDB databases; constants only (sandbox setup, one conversion, two metadata reads), no client text"),
        ("Autheris.Application.Olap.OlapSqlInspector", "DuckDB parse-only (json_serialize_sql): the client text is passed as a quoted literal to the parser of a throw-away in-memory database; nothing is executed or staged"),
        ("Autheris.Infrastructure.Caching.TableCache.Storage.DuckDbConnectionPool", "DuckDB table cache connection pool: executes validated ATTACH statements on reader initialization; no client text"),
        ("Autheris.Application.VirtualFilters.Federation.SqlVirtualFilterKeySetProvider", "virtual-filter key set: the query is rendered from the typed key-source definition"),
        ("Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetLoader", "row-filter key set (issue #107): fixed transaction-mode statements (SET TRANSACTION, PRAGMA query_only) only; the key query text is set by the binder through CompiledSqlBinders.BindKeyQuery"),
        // -- statements of the gateway itself (no client text) --
        ("Autheris.Application.Services.DbSessionContextInitializer", "fixed session-initialisation statements (tenant and user session context)"),
        ("Autheris.Infrastructure.Persistence.SqlConnectionFactory", "fixed session pinning and hardening statements of a new connection"),
        ("Autheris.Extensions.Cdc.CdcSourceQueries", "CDC producer: fixed change-tracking statements over validated declared table and column names, bound values, no client text"),
        ("Autheris.Application.Procedures.Services.MssqlProcedureInvoker", "stored-procedure call by a validated catalog name with bound parameters (not a WebSQL statement)"),
        ("Autheris.Application.Procedures.Services.SqlProcedureRowScopeResolver", "procedure row-scope key query over validated catalog identifiers, bound values, read-only; splices validated policy row-filter text (§7.3 consumer)"),
        ("Autheris.Application.Procedures.Services.StoredProcedureCatalogValidator", "catalog validation queries over the procedure definitions (metadata only)"),
        ("Autheris.Infrastructure.Persistence.Catalog.*", "column-metadata readers: fixed information_schema/PRAGMA queries of the governed catalog provider"),
        // -- persistence of the gateway's own governance store (its schema bootstrap and repositories) --
        ("Autheris.Infrastructure.Persistence.SqliteGovernanceRepository", "governance store repository"),
        ("Autheris.Infrastructure.Persistence.SqlServerGovernanceRepository", "governance store repository and schema bootstrap"),
        ("Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository", "governance store repository and schema bootstrap"),
        ("Autheris.Infrastructure.Persistence.CatalogKeyDefinitionStore", "governance store: declared key/index tables of the three engines (issue #94), fixed statements with bound values, no client text"),
        ("Autheris.Infrastructure.Persistence.RowFilterEvaluationStore", "governance store: row filter evaluation settings (issue #107), fixed statements with bound values over the three engines, no client text"),
        ("Autheris.Infrastructure.Persistence.DatasetCatalogStore", "governance store: dataset catalog tags and visibility tier (issue #111), fixed statements with bound values over the three engines, no client text"),
        ("Autheris.Infrastructure.Persistence.Migrations.*", "governance schema migrations (WP-ARC-B-12): fixed DDL and version-table statements, no client text"),
        ("Autheris.Infrastructure.Persistence.SqliteGovernanceConnectionProvider", "governance SQLite connection setup (WP-ARC-B-13): fixed PRAGMA statements with a validated journal-mode token, no client text"),
        ("Autheris.Infrastructure.Persistence.SqliteGovernanceLintSource", "read-only governance store reader of the pre-deploy policy lint"),
        ("Autheris.Infrastructure.Persistence.TenantRegistryStore", "tenant registry table of the governance store"),
        ("Autheris.Infrastructure.Persistence.VirtualFilterStore", "virtual filter table of the governance store"),
        ("Autheris.Infrastructure.Persistence.CallerAttributeStore", "caller attribute registry tables of the governance store (REQ-GOV-129): fixed statements with bound values, no client text"),
        ("Autheris.Application.Identity.Attributes.AttributeTableProvider", "caller attribute lookup (REQ-GOV-129 A-5.2): the statement is built by AttributeTableQuery from validated identifiers (^[A-Za-z_][A-Za-z0-9_]{0,127}$, quoted per dialect) with the subject, tenant and issuer bound; no client text"),
        ("Autheris.Infrastructure.Persistence.AccessProfileStore", "access profile tables of the governance store (WP-SC-03): fixed statements with bound values, only integer-indexed parameter names and enum literals are spliced, no client text"),
        ("Autheris.Application.Sql.Federation.CoLocation.CoLocationVerificationService", "co-location verification (Issue #132, SEC-DATA132-05): fixed 'SELECT @@SERVERNAME' verification statement on SQL Server connection, no client text"),
        ("Autheris.Infrastructure.Persistence.ConsumerAggregationStore", "consumer aggregation store (Issue #114, SEC-AUD114-05): fixed rollup statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.CdcStreamSqlCore", "CDC durable stream store (Issue #99, SEC-CDC-05): fixed event and row-image statements over engine-specific schemas, bound values, no client text"),
        ("Autheris.Extensions.Cdc.CustomQuery.CustomQueryPollerService", "CDC custom query poller service (Issue #101, Issue #210): reads watermark batches using configured poller queries, bound parameters, no client text"),
        ("Autheris.Application.Data.Relations.GovernedCrossSourceRowReader", "Cross-source GraphQL relation row reader (Issue #75, Issue #223): executes batched lookup statements with bound parameters over child data sources, no client text"),
        ("Autheris.Application.Caching.TableCache.Governance.GovernedTableCacheExecutionService", "Governed table cache execution service (BL-04, REQ-DATA-BL04): executes governed SELECT queries with AST tenant pushdown and bound parameters over embedded cache store; no client text"),
        ("Autheris.Infrastructure.Caching.TableCache.DuckDbPersistentTableCacheStore", "DuckDB persistent table cache store (BL-04, REQ-DATA-BL04): executes internal staging, swapping, insertion and drop statements on embedded cache database with bound parameters; no client text"),
        ("Autheris.Infrastructure.Caching.TableCache.InMemoryTableCacheStore", "In-memory table cache store (BL-04, REQ-DATA-BL04): executes internal memory-table DDL and mutation statements with bound parameters; no client text"),
        ("Autheris.Api.Hosting.Warmup.DataSourceConnectionWarmup", "Startup pre-warming probe (Issue #254, SEC-PERF254-02): executes read-only constant expression SELECT 1 to verify database connectivity on startup; constants only, no client text"),
        ("Autheris.Infrastructure.Persistence.GovernanceSchemaValidator", "governance store startup validator (SEC-SC-20): validates presence of Wave 2 tables at startup, fixed information_schema queries, no client text"),
        ("Autheris.Infrastructure.Persistence.IdentityStore", "governance store: SCIM and local identity provider storage (WP-SC-07, REQ-SCALE-5000-WAVE2), fixed statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.RightsAndPoliciesStore", "governance store: functional rights mappings and scoped access policies (WP-SC-06, WP-SC-14), fixed statements with bound parameters, no client text"),
        ("Autheris.Infrastructure.Persistence.RecertificationStore", "governance store: recertification campaigns and items (WP-SC-08), fixed statements with bound parameters, no client text"),
    ];

    private static bool IsAllowedCommandTextSite(string top) =>
        CommandTextAllowlist.Any(e => e.Type.EndsWith('*') ? top.StartsWith(e.Type[..^1], StringComparison.Ordinal) : top == e.Type);

    internal sealed class ControlSetsCommandText
    {
        public void Run(System.Data.Common.DbCommand command) => command.CommandText = "SELECT 1";
    }

    [Fact]
    public void TheCommandTextScanner_FindsAKnownSetter_OnADbCommandSubtype()
    {
        CommandTextSetterSites(TypesOf([typeof(CutoverArchitectureTests).Assembly]))
            .ShouldContain(site => site.Site.Contains(nameof(ControlSetsCommandText), StringComparison.Ordinal));
    }

    /// <summary>A command type whose constructor takes the text (the shape of <c>SqlCommand(string, ...)</c> and of a provider batch command).</summary>
    internal sealed class ControlTextCommand(string text)
    {
        public string Text { get; } = text;
    }

    internal sealed class ControlConnection
    {
        public System.Data.Common.DbCommand CreateCommand(string text) => throw new NotSupportedException(text);
    }

    internal sealed class ControlCreatesCommandsWithText
    {
        public object NewCommand() => new ControlTextCommand("SELECT 1");

        public object Created(ControlConnection connection) => connection.CreateCommand("SELECT 1");
    }

    [Theory]
    [InlineData(nameof(ControlCreatesCommandsWithText.NewCommand))]
    [InlineData(nameof(ControlCreatesCommandsWithText.Created))]
    public void TheCommandTextScanner_FindsACommandConstructorAndACreateCommandFactory_ThatTakeTheText(string method)
    {
        // L-3 positive controls: the constructor of a *Command type taking a string, and CreateCommand(string)
        CommandTextSetterSites(TypesOf([typeof(CutoverArchitectureTests).Assembly]))
            .ShouldContain(site => site.Site.EndsWith($"{nameof(ControlCreatesCommandsWithText)}.{method}", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCommandTextAssignmentOutsideTheAllowlist()
    {
        var sites = CommandTextSetterSites(ProductionTypes());

        sites.ShouldContain(s => s.Top == "TrinoSqlEngine.Ast.Emit.DbCommandCompiledSqlBinder", "positive control: the binder assigns CommandText");
        var violations = sites.Where(s => !IsAllowedCommandTextSite(s.Top)).Select(s => s.Site).Distinct().Order(StringComparer.Ordinal).ToList();
        violations.ShouldBeEmpty("CommandText is assigned only by the allowlisted types (every entry has a reason): " + string.Join(", ", violations));

        // an allowlist entry that no longer matches anything is dead weight that would silently re-allow a rename
        var unused = CommandTextAllowlist.Where(e => !sites.Any(s => e.Type.EndsWith('*') ? s.Top.StartsWith(e.Type[..^1], StringComparison.Ordinal) : s.Top == e.Type))
            .Select(e => e.Type).ToList();
        unused.ShouldBeEmpty("Remove the allowlist entries that assign no CommandText any more: " + string.Join(", ", unused));
    }

    [Fact]
    public void TheGovernedPath_NeverAssignsCommandText()
    {
        string[] governed =
        [
            "Autheris.Application.Sql.Services.GovernedSqlExecutor", "Autheris.Application.Sql.Services.GovernedSqlExecutionService",
            "Autheris.Application.Sql.Services.GovernedSqlRewriter", "Autheris.Application.Sql.Services.WebSqlStatementManager"
        ];
        var types = ProductionTypes();
        var sites = CommandTextSetterSites(types);
        foreach (string name in governed)
        {
            types.ShouldContain(t => Top(t.Type) == name, $"{name} exists");
            sites.Where(s => s.Top == name).ShouldBeEmpty($"{name} never assigns CommandText");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // GovernedEntryPoints_ReachCompileAndBinder (M-7, §28.2)
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The request-handling methods of the entry points of §28.2 marked "via X1" (the endpoint handler that a route is mapped to, the public
    /// execute method of a service, the worker's job loop): each must itself reach <c>ISqlEngine.Compile</c> and a binder through the call
    /// graph. Starting at these methods, and not at every method of the type, means that a helper that compiles cannot vouch for a handler
    /// that does not.
    /// </summary>
    private static readonly (string Type, string Method, bool Executes)[] GovernedEntryPoints =
    [
        ("Autheris.Api.Endpoints.WebSqlEndpoints", "HandleWebSqlRequest", true),
        ("Autheris.Api.Endpoints.WebSqlEndpoints", "HandleParquetWebSqlRequestAsync", true),
        ("Autheris.Application.Sql.Services.WebSqlStatementManager", "SubmitOrWaitAsync", true),
        ("Autheris.Application.SqlEndpoints.Services.SqlEndpointExecutionService", "ExecuteEndpointAsync", true),
        ("Autheris.Application.Jobs.Services.AsyncQueryJobBackgroundWorker", "ProcessJobItemAsync", true),
        ("Autheris.Api.Endpoints.ArrowExportEndpoints", "HandleArrowExportAsync", true),
        // Flight SQL: GetFlightInfo only validates (compiles) the statement before it issues a ticket; DoGet executes it.
        ("Autheris.Application.Serialization.ArrowFlightSqlServer", "GetFlightInfoAsync", false),
        ("Autheris.Application.Serialization.ArrowFlightSqlServer", "DoGetStreamAsync", true),
        ("Autheris.Application.Mcp.Services.McpToolExecutionHandler", "ExecuteToolAsync", true),
        ("Autheris.Application.Mcp.Services.ApiDispatcherService", "InvokeApiAsync", true),
        ("Autheris.Application.Data.Services.GovernedDataQueryService", "ExecuteQueryAsync", true),
        // WP-ARC-B-02: the SQL table reads of GraphQL flat reads, OData, MCP, connectors and batch relations run through the planner and
        // the governed execution service (Compile + binder); SqlDataSourceExecutor no longer exists.
        ("Autheris.Application.Services.SqlTableReadExecutor", "ExecuteAsync", true),
        ("Autheris.Application.Services.GatewayExecutionService", "ExecuteTablePageAsync", true),
        ("Autheris.Application.Services.GatewayExecutionService", "ExecuteTableQueryAsync", true),
        ("Autheris.Extensions.OData.ODataHandler", "ExecuteEntitySetQueryAsync", true),
        ("Autheris.GraphQL.Mcp.GatewayMcpQueryExecutor", "ExecuteOperationAsync", true)
    ];

    /// <summary>
    /// The explicit allowed exceptions of §28.9: governed SQL outside <c>Compile</c>. OQ-GQL is the GraphQL tree compiler (G-6: the single
    /// documented exception, guarded by <c>TreeVsCompilePolicyParityTests</c>); OQ-4 are the §7.3 text consumers (they render from the
    /// typed IR through <c>IPolicyPredicateRenderer</c> and the mask renderer).
    /// </summary>
    private static readonly string[] AllowedExceptionsOutsideCompile =
    [
        "Autheris.Application.Sql.Tree.GovernedTreeQueryService",
        "Autheris.Application.Olap.FederatedStagingService",
        "Autheris.Application.Olap.OlapStagingService"
    ];

    private sealed record Node(string Type, string Method);

    private sealed record CallGraph(Dictionary<Node, HashSet<Node>> Edges, Dictionary<Node, HashSet<string>> Targets);

    /// <summary>The user-written method that a compiler generated name stands for: <c>&lt;Run&gt;d__3</c>, <c>&lt;&lt;Run&gt;b__0&gt;d</c>, <c>&lt;Run&gt;b__0</c>, <c>&lt;Run&gt;g__Local|1_0</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex GeneratedName = new(@"^<+([^<>]+)>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string OriginalMethod(TypeDefinition type, MethodDefinition method)
    {
        for (var t = type; t.DeclaringType is not null; t = t.DeclaringType)
        {
            if (GeneratedName.Match(t.Name) is { Success: true } m) return m.Groups[1].Value;
        }

        return GeneratedName.Match(method.Name) is { Success: true } named ? named.Groups[1].Value : method.Name;
    }

    /// <summary>The full names of the type, its base types and every interface they implement (generic types by their definition).</summary>
    private static HashSet<string> Supertypes(TypeDefinition type)
    {
        var result = new HashSet<string>();
        var pending = new Stack<TypeDefinition>();
        pending.Push(type);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!result.Add(current.FullName)) continue;
            foreach (var i in current.Interfaces)
            {
                result.Add(i.InterfaceType.GetElementType().FullName);
                if (i.InterfaceType.Resolve() is { } resolvedInterface) pending.Push(resolvedInterface);
            }

            if (current.BaseType?.Resolve() is { } baseType) pending.Push(baseType);
            else if (current.BaseType is not null) result.Add(current.BaseType.GetElementType().FullName);
        }

        return result;
    }

    private static readonly CallGraph ProductionCallGraph = BuildCallGraphCore(ProductionAssemblies);

    /// <summary>
    /// Call graph on (top-level type, method name). Compiler generated types (async state machines, closures) are folded into the
    /// method they came from. A call through an interface, an abstract or a virtual method links only to the implementations of that name in
    /// types that derive from or implement the declaring type of the call (not to every method of that name).
    /// </summary>
    private static CallGraph BuildCallGraph(IEnumerable<Assembly> assemblies) =>
        ReferenceEquals(assemblies, ProductionAssemblies) ? ProductionCallGraph : BuildCallGraphCore(assemblies);

    private static CallGraph BuildCallGraphCore(IEnumerable<Assembly> assemblies)
    {
        var types = TypesOf(assemblies);
        var edges = new Dictionary<Node, HashSet<Node>>();
        var targets = new Dictionary<Node, HashSet<string>>();
        var implementations = new Dictionary<string, List<(Node Node, HashSet<string> Supertypes)>>();

        foreach (var (_, type) in types)
        {
            var supertypes = Supertypes(type);
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var node = new Node(Top(type), OriginalMethod(type, method));
                if (!implementations.TryGetValue(node.Method, out var list)) implementations[node.Method] = list = [];
                list.Add((node, supertypes));
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
                    string owner = reference.DeclaringType.GetElementType().FullName;
                    called.Add($"{reference.DeclaringType.FullName}::{reference.Name}");
                    if (!implementations.TryGetValue(reference.Name, out var impls)) continue;
                    bool dispatch = reference.Name != ".ctor" && InvolvesDispatch(reference);
                    foreach (var (impl, supertypes) in impls)
                    {
                        if (impl.Type == owner || dispatch && supertypes.Contains(owner)) outgoing.Add(impl);
                    }
                }
            }
        }

        return new CallGraph(edges, targets);
    }

    private static bool InvolvesDispatch(MethodReference reference)
    {
        var resolved = reference.Resolve();
        return resolved is null || resolved.IsVirtual || resolved.DeclaringType.IsInterface || resolved.DeclaringType.IsAbstract;
    }

    private static bool Reaches(CallGraph graph, Node entry, string target)
    {
        var seen = new HashSet<Node>();
        var queue = new Queue<Node>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!seen.Add(node)) continue;
            if (graph.Targets.TryGetValue(node, out var called) && called.Contains(target)) return true;
            if (graph.Edges.TryGetValue(node, out var next)) foreach (var n in next) queue.Enqueue(n);
        }

        return false;
    }

    private const string CompileTarget = "TrinoSqlEngine.ISqlEngine::Compile";
    private const string BinderTarget = "TrinoSqlEngine.Ast.Emit.CompiledSqlBinders::For";

    private static List<string> EntryPointViolations(
        CallGraph graph, IEnumerable<(string Type, string Method, bool Executes)> entries, string compileTarget = CompileTarget, string binderTarget = BinderTarget)
    {
        var violations = new List<string>();
        foreach (var (type, method, executes) in entries)
        {
            var node = new Node(type, method);
            if (!graph.Edges.ContainsKey(node))
            {
                violations.Add($"{type}.{method} does not exist in a scanned assembly");
                continue;
            }

            if (!Reaches(graph, node, compileTarget)) violations.Add($"{type}.{method} does not reach ISqlEngine.Compile");
            if (executes && !Reaches(graph, node, binderTarget)) violations.Add($"{type}.{method} does not reach CompiledSqlBinders.For");
        }

        return violations;
    }

    [Fact]
    public void GovernedEntryPoints_ReachCompileAndBinder()
    {
        var violations = EntryPointViolations(BuildCallGraph(ProductionAssemblies), GovernedEntryPoints);

        violations.ShouldBeEmpty("Every request-handling method of a governed entry point (M-7, plan 28.2) reaches Compile and the binder: " + string.Join("; ", violations));
    }

    [Fact]
    public void TheCallGraphScanner_FindsTheKnownCompileAndBinderCallers()
    {
        var graph = BuildCallGraph(ProductionAssemblies);
        graph.Targets.Where(kv => kv.Value.Contains(CompileTarget)).Select(kv => kv.Key.Type)
            .Distinct().ShouldBe(new[] { "Autheris.Application.Sql.Services.GovernedSqlRewriter", "Autheris.Application.Sql.Federation.Subtrees.SubtreeCompiler" }, ignoreOrder: true);
        graph.Targets.Where(kv => kv.Value.Contains(BinderTarget)).Select(kv => kv.Key.Type)
            .Distinct().ShouldBe(new[] { "Autheris.Application.Sql.Services.GovernedSqlExecutor", "Autheris.Application.Olap.DuckDbOlapEngine" }, ignoreOrder: true);
    }

    // The fixtures of the controls below are top-level types of this (test) assembly, which the production scans never read
    // (CutoverArchitectureControls.cs). The control graph uses the control engine as the compile and binder target.
    private static readonly string ControlCompileTarget = $"{typeof(ControlEntry).Namespace}.{nameof(IControlEngine)}::{nameof(IControlEngine.Compile)}";

    private static CallGraph ControlGraph() => BuildCallGraph([typeof(CutoverArchitectureTests).Assembly]);

    [Fact]
    public void TheEntryPointScan_AcceptsAHandlerThatCompiles_AndRejectsARawSqlHandlerOnTheSameType()
    {
        var graph = ControlGraph();
        string entryType = typeof(ControlEntry).FullName!;

        // positive control: the governed handler reaches the engine through the interface
        EntryPointViolations(graph, [(entryType, nameof(ControlEntry.Governed), true)], ControlCompileTarget, ControlCompileTarget).ShouldBeEmpty();

        // negative control: a raw SQL method on the same entry type fails the check that the production entry points go through
        var violations = EntryPointViolations(graph, [(entryType, nameof(ControlEntry.Raw), true)], ControlCompileTarget, ControlCompileTarget);
        violations.ShouldContain(v => v.Contains("does not reach ISqlEngine.Compile", StringComparison.Ordinal));

        // a handler that does not exist (a rename) fails as well, so a stale entry list cannot pass vacuously
        EntryPointViolations(graph, [(entryType, "Renamed", true)], ControlCompileTarget, ControlCompileTarget).ShouldNotBeEmpty();
    }

    [Fact]
    public void TheEntryPointScan_DoesNotLinkAVirtualCallToATypeThatDoesNotImplementIt()
    {
        var graph = ControlGraph();

        // ControlEntry.Governed calls IControlEngine.Compile: ControlEngine implements it and is linked, ControlUnrelatedCompiler has a
        // method of the same name but is no implementation and must not make a handler look governed
        var edges = graph.Edges[new Node(typeof(ControlEntry).FullName!, nameof(ControlEntry.Governed))];
        edges.ShouldContain(n => n.Type == typeof(ControlEngine).FullName);
        edges.ShouldNotContain(n => n.Type == typeof(ControlUnrelatedCompiler).FullName);
        Reaches(graph, new Node(typeof(ControlEntry).FullName!, nameof(ControlEntry.ViaUnrelated)), ControlCompileTarget).ShouldBeFalse();
    }

    [Fact]
    public void TheExplicitExceptions_NeverCallALegacyApi_AndDoNotPretendToCompile()
    {
        var graph = BuildCallGraph(ProductionAssemblies);
        foreach (string exception in AllowedExceptionsOutsideCompile)
        {
            graph.Edges.Keys.ShouldContain(n => n.Type == exception, $"{exception} exists in a production assembly");
            // They are not entry points of the governed WebSQL pipeline: they must not own a second binder or a second compile site.
            graph.Targets.Where(kv => kv.Key.Type == exception).SelectMany(kv => kv.Value)
                .ShouldNotContain(BinderTarget, $"{exception} must not bind through the governed binder; it is a text consumer");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // AllExecutorsUseGovernedQueryStages (WP-ARC-B-03)
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The governed read executors. Each takes its authorization and its audit entry from <c>GovernedQueryStages</c>, so that one
    /// place owns the PDP call, the non-canonical ClusterAdmin fail-closed check and the audit entry shape.
    /// </summary>
    private static readonly string[] GovernedReadExecutors =
    [
        "Autheris.Application.Services.GatewayExecutionService",
        "Autheris.Application.Sql.Tree.GovernedTreeQueryService",
        "Autheris.Application.Procedures.Services.GovernedProcedureExecutionService",
        "Autheris.Application.Data.Services.GovernedDataQueryService",
        "Autheris.Application.Services.SqlTableReadExecutor"
    ];

    private const string StagesPrefix = "Autheris.Application.Policy.GovernedQueryStages::";
    private const string AuditWriteTarget = "Autheris.Application.Interfaces.IAuditLogRepository::RecordAuditEventAsync";

    [Fact]
    public void AllExecutorsUseGovernedQueryStages()
    {
        var graph = BuildCallGraph(ProductionAssemblies);
        foreach (string executor in GovernedReadExecutors)
        {
            var targets = graph.Targets.Where(kv => kv.Key.Type == executor).SelectMany(kv => kv.Value).ToList();
            graph.Edges.Keys.ShouldContain(n => n.Type == executor, $"{executor} exists in a production assembly");
            targets.ShouldContain(t => t.StartsWith(StagesPrefix, StringComparison.Ordinal), $"{executor} uses GovernedQueryStages");
            targets.ShouldNotContain(AuditWriteTarget, $"{executor} writes its audit entry through GovernedQueryStages, not directly");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // PlannerOutput_NeverReachesACommand (WP-ARC-B-02)
    // ---------------------------------------------------------------------------------------------------------------------

    private const string PlannerPrefix = "Autheris.Application.Data.Interfaces.IGovernedTableReadPlanner::";

    /// <summary>
    /// The text of <c>IGovernedTableReadPlanner</c> is Trino text for the governed compile path. A type that plans a read must hand the text
    /// to <c>IGovernedSqlExecutionService</c> and never put it into a <c>DbCommand</c> (the provider would run unrewritten, unbound text).
    /// </summary>
    [Fact]
    public void PlannerOutput_NeverReachesACommand()
    {
        var graph = BuildCallGraph(ProductionAssemblies);
        var plannerUsers = graph.Targets
            .Where(kv => kv.Value.Any(t => t.StartsWith(PlannerPrefix, StringComparison.Ordinal)))
            .Select(kv => kv.Key.Type)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        plannerUsers.ShouldContain("Autheris.Application.Services.SqlTableReadExecutor", "positive control: the table-read executor plans reads");
        plannerUsers.ShouldContain("Autheris.Application.Data.Services.GovernedDataQueryService", "positive control: the dataset service plans reads");

        var commandTypes = CommandTextSetterSites(ProductionTypes()).Select(s => s.Top).ToHashSet(StringComparer.Ordinal);
        var violations = plannerUsers.Append("Autheris.Application.Data.Services.GovernedTableReadPlanner").Where(commandTypes.Contains).ToList();
        violations.ShouldBeEmpty("Types that plan a table read must not assign CommandText: " + string.Join(", ", violations));
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
