namespace TrinoSqlEngine;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// The single governed compile pipeline (plan 4.7):
/// dialect gate, token guards and parse, build, validate, simplify the user tree, inject typed security, verify coverage,
/// validate capabilities, emit, check limits and the emitted text, cache the value-free template.
/// Nothing is emitted unless every step passed (fail closed).
/// </summary>
internal sealed class GovernedSqlCompiler
{
    private const int MinimumInputForExpansion = 64;
    private const StatementPermissions AllDml =
        StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge;
    private static readonly IReadOnlyDictionary<string, object?> ClientNoValues = new Dictionary<string, object?>();

    private readonly FastSqlEngine _engine;
    private readonly IDialectCapabilityProvider _capabilities;
    private readonly CompiledSqlTemplateCache _cache;

    public GovernedSqlCompiler(FastSqlEngine engine, IDialectCapabilityProvider capabilities, CompiledSqlTemplateCache cache)
    {
        _engine = engine;
        _capabilities = capabilities;
        _cache = cache;
    }

    public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = CompileLimits.Normalize(request);   // CR-ADG-14, CR-ADG-15
        using var activity = CompilerTelemetry.Source.StartActivity("sql.compile");
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.CompileTimeout);

        try
        {
            var compiled = CompileCore(sql, request, clock, budget.Token);
            activity?.SetTag("sql.target_dialect", compiled.Dialect.ToString());
            activity?.SetTag("sql.statement_class", compiled.StatementClass.ToString());
            activity?.SetTag("sql.compiler_version", compiled.CompilerVersion);
            activity?.SetTag("sql.bind_count", compiled.Parameters.Length);
            return compiled;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested && ex is not Antlr4.Runtime.Misc.ParseCanceledException)
        {
            var limit = new SqlLimitExceededException(SqlLimitKind.CompileTime, request.TargetDialect, clock.ElapsedMilliseconds, (long)request.CompileTimeout.TotalMilliseconds);
            CompilerTelemetry.RecordRejected(request.TargetDialect, limit);
            throw limit;
        }
        catch (Exception ex)
        {
            CompilerTelemetry.RecordRejected(request.TargetDialect, ex);
            throw;
        }
    }

    /// <summary>Test seam hook (Debug builds only; the call is removed from Release builds).</summary>
    [Conditional("DEBUG")]
    private static void Pass(string name)
    {
#if DEBUG
        CompilerTestSeams.Current?.OnPass?.Invoke(name);
#endif
    }

    private static void CheckBudget(CompileRequest request, Stopwatch clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (clock.Elapsed >= request.CompileTimeout)
        {
            throw new SqlLimitExceededException(SqlLimitKind.CompileTime, request.TargetDialect, clock.ElapsedMilliseconds, (long)request.CompileTimeout.TotalMilliseconds);
        }
    }

    private CompiledSql CompileCore(ReadOnlyMemory<char> sql, CompileRequest request, Stopwatch clock, CancellationToken token)
    {
        // 1. dialect and statement gates (fail closed, typed "not yet supported")
        DialectCapabilities caps;
        try
        {
            caps = _capabilities.Get(request.TargetDialect);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Dialect, request.TargetDialect.ToString());
        }

        if (caps.Tier != DialectSupportTier.Production && !request.AllowExperimentalDialect)
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Dialect, request.TargetDialect.ToString());
        }

        if ((request.Statements & ~AllDml) != 0)
        {
            // An unknown permission bit is never interpreted (fail closed).
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, request.Statements.ToString());
        }

        if (request.Policy.Dml is null || request.Policy.Dml != DmlGuardOptions.Strict)
        {
            // CR-ADG-38: guards are not a per-request choice; a relaxed switch is a configuration error, never a silent no-op.
            throw new SqlCompileConfigurationException();
        }

        CheckBudget(request, clock, token);

        // 2. cache: a hit still validates every table dependency and rebinds the current values (INV-12)
        string rawSql = sql.ToString();
        string keyMaterial = CompileCacheKey.Material(request, rawSql, caps, _engine.MaxQueryLength);
        var policy = request.Policy;
        var typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps) { Dml = policy.Dml };

        var template = _cache.Find(keyMaterial);
        if (template is not null && TryRehydrate(template, typed))
        {
            _cache.CountHit();
            return template.Rebind(typed.PolicyValues);
        }

        _cache.CountMiss();
        if (template is not null)
        {
            // A rehydration attempt may have added values; start clean. A plain miss reuses the context (CR-ADG-12).
            typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps) { Dml = policy.Dml };
        }

        // 3. token guards + parse
        Pass("parse");
        var (tree, _) = _engine.Parse(sql, request.TokenGuards, token);
        var statementClass = ClassifyStatement(tree);
        EnsureStatementAllowed(statementClass, request, caps);
        CheckBudget(request, clock, token);

        // 4. build + validate the user tree
        Pass("build");
        var builderOptions = new AstBuilderOptions
        {
            EnforceReadOnlyQueries = statementClass == SqlStatementClass.Select,
            EnforceFunctionPolicy = true,
            AllowedFunctions = EffectiveFunctions(request, caps),
            AllowedTableFunctions = request.AllowedTableFunctions,
            TranslateTrinoDateFunctions = request.TranslateTrinoDateFunctions,
            RejectTimeTravelQueries = true
        };
        var ast = new SqlAstBuilder(builderOptions).BuildStatement(tree);
        Pass("validate");
        new AstValidationVisitor { CancellationToken = token }.Validate(ast);

        // 5. simplify the USER tree only: injected predicates do not exist yet (INV-3 by construction)
        Pass("simplify");
        var simplified = (SqlStatement)new AstSimplificationVisitor { CancellationToken = token }.Visit(ast);
        CheckBudget(request, clock, token);

        // 6. typed security injection
        Pass("inject");
        var secured = (SqlStatement)new AstSecurityVisitor(BuildOptions(request, typed), _engine, typed)
        {
            CancellationToken = token
        }.Visit(simplified);
        CheckBudget(request, clock, token);

#if DEBUG
        if (CompilerTestSeams.Current?.FaultyInjector is { } faultyInjector)
        {
            secured = (SqlStatement)faultyInjector(secured);
        }
#endif

        // 7. production coverage proof (does not trust the injector)
        Pass("verify");
        var applied = typed.CreateVerifier(request.TargetDialect).Verify(secured, token);

        // 8. dialect capabilities
        Pass("capabilities");
        DialectCapabilityValidator.Validate(secured, caps);

        // 9. emit (+ bind limit and emitted-text checker inside)
        Pass("emit");
        var generator = SqlDialectGeneratorFactory.GetGenerator(request.TargetDialect);
        var compiled = generator.Generate(secured, new ParameterSource(typed.PolicyValues, ClientNoValues), token)
            with { AppliedPredicates = applied };

        // 10. output budget
        long limit = (long)request.MaxExpansionFactor * Math.Max(rawSql.Length, MinimumInputForExpansion);
        if (compiled.Sql.Length > limit)
        {
            throw new SqlLimitExceededException(SqlLimitKind.PolicyExpansionFactor, request.TargetDialect, compiled.Sql.Length, limit);
        }

        CheckBudget(request, clock, token);

        // 11. only verified output is cached, value-free
        _cache.Add(CompiledSqlTemplate.From(keyMaterial, compiled, typed.Dependencies));
        return compiled;
    }

    /// <summary>
    /// CR-ADG-02 / INV-1 / plan 3.6: the allowlist is the dialect's function map. A caller list can only narrow it; a function
    /// without a rule is rejected, never passed through. <c>null</c> means "the dialect map", not "everything not denied".
    /// </summary>
    private static IReadOnlySet<string> EffectiveFunctions(CompileRequest request, DialectCapabilities caps)
    {
        var map = caps.Functions.Names;
        if (request.AllowedFunctions is null)
        {
            return map;
        }

        var narrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.AllowedFunctions)
        {
            if (map.Contains(name)) narrowed.Add(name);
        }

        return narrowed;
    }

    private static bool TryRehydrate(CompiledSqlTemplate template, TypedPolicyContext typed)
    {
        foreach (var dependency in template.Dependencies)
        {
            var entry = typed.Catalog.Resolve(dependency.Name);
            if (entry is null || !string.Equals(typed.Fingerprint(entry), dependency.Fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            typed.AddTableValues(entry);
        }

        return true;
    }

    private static SqlStatementClass ClassifyStatement(SqlBaseParser.SingleStatementContext tree) => tree.statement() switch
    {
        SqlBaseParser.StatementDefaultContext => SqlStatementClass.Select,
        SqlBaseParser.InsertIntoContext => SqlStatementClass.Insert,
        SqlBaseParser.UpdateContext => SqlStatementClass.Update,
        SqlBaseParser.DeleteContext => SqlStatementClass.Delete,
        SqlBaseParser.MergeContext => SqlStatementClass.Merge,
        _ => throw new SecurityException("Only read queries and the permitted DML statements are accepted by the governed SQL compiler.")
    };

    /// <summary>
    /// A DML class compiles only when the request permits it and the dialect's capability entry lists it (fail closed, typed).
    /// </summary>
    private static void EnsureStatementAllowed(SqlStatementClass statementClass, CompileRequest request, DialectCapabilities caps)
    {
        var required = statementClass switch
        {
            SqlStatementClass.Select => StatementPermissions.ReadOnly,
            SqlStatementClass.Insert => StatementPermissions.Insert,
            SqlStatementClass.Update => StatementPermissions.Update,
            SqlStatementClass.Delete => StatementPermissions.Delete,
            SqlStatementClass.Merge => StatementPermissions.Merge,
            _ => throw new SecurityException("Unknown statement class.")
        };

        if (required == StatementPermissions.ReadOnly)
        {
            return;
        }

        if (!request.Statements.HasFlag(required) || !caps.DmlStatements.HasFlag(required) ||
            (statementClass == SqlStatementClass.Merge && !caps.SupportsMerge))
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, statementClass.ToString());
        }
    }

    private static RlsOptions BuildOptions(CompileRequest request, TypedPolicyContext typed)
    {
        var catalog = typed.Catalog;
        return new RlsOptions
        {
            // The typed path never consults the string policy provider; any use fails loudly.
            PolicyProvider = new UnusedPolicyProvider(),
            ColumnMaskingProvider = new CatalogMaskGuard(catalog, typed.Masks),
            TableColumnsProvider = name => catalog.Resolve(ParseName(name))?.Columns.Select(c => c.Name).ToList(),
            EnforceCatalogProjection = true,
            EnforcedMaxRows = request.EnforcedMaxRows,
            EnforceReadOnlyQueries = true,
            TargetDialect = request.TargetDialect,
            TablesWithMaskedColumns = new HashSet<string>(request.Policy.TablesWithMaskedColumns, StringComparer.OrdinalIgnoreCase),
            TablesWithConsentRowFilter = new HashSet<string>(request.Policy.TablesWithConsentRowFilter, StringComparer.OrdinalIgnoreCase),
            RejectMaskedColumnsInPredicates = true,
            // DML guards of the request (DmlGuardOptions.Strict by default); the coverage verifier proves what they require.
            EnforceWithCheckOption = request.Policy.Dml.EnforceWithCheckOption,
            RequireTenantColumnInInsert = request.Policy.Dml.RequireTenantColumnInInsert,
            DisallowTenantColumnModificationInUpdate = request.Policy.Dml.DisallowTenantColumnModificationInUpdate,
            RejectUnfilteredDml = request.Policy.Dml.RejectUnfilteredDml,
            RejectMaskedColumnsInDml = request.Policy.Dml.RejectMaskedColumnsInDml,
            RejectConsentFilteredInsert = request.Policy.Dml.RejectConsentFilteredInsert,
            RejectWholeRowReferencesInDml = request.Policy.Dml.RejectWholeRowReferencesInDml,
            RejectPolicyColumnAssignment = request.Policy.Dml.RejectPolicyColumnAssignment
        };
    }

    private static SqlQualifiedName ParseName(string normalized) =>
        new(normalized.Split('.').Select(p => new SqlIdentifier(p)).ToList());

    private sealed class UnusedPolicyProvider : IRlsPolicyProvider
    {
        public bool ShouldApplyPolicy(string tableName) => throw new InvalidOperationException("The string policy provider is not used by the typed compiler.");
        public string GetPolicyFilter(string tableName) => throw new InvalidOperationException("The string policy provider is not used by the typed compiler.");
    }

    /// <summary>Lets the masked-column predicate guards ask the typed mask provider; it never renders SQL.</summary>
    private sealed class CatalogMaskGuard : IColumnMaskingPolicyProvider
    {
        private readonly ITableCatalog _catalog;
        private readonly IColumnMaskProvider _masks;

        public CatalogMaskGuard(ITableCatalog catalog, IColumnMaskProvider masks)
        {
            _catalog = catalog;
            _masks = masks;
        }

        public bool HasMask(string tableName, string columnName)
        {
            var entry = _catalog.Resolve(ParseName(tableName));
            return entry is not null && _masks.HasMask(entry.Identity, columnName);
        }

        public string GetMaskedExpression(string tableName, string columnName) =>
            throw new InvalidOperationException("Mask expressions are typed nodes on the compiler path.");
    }
}
