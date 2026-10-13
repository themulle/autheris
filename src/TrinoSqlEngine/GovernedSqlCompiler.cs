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
using TrinoSqlEngine.Ast.Security;
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

    /// <summary>The DML guards the pipeline applies: the validated request guards (strict), except in a Debug seam that runs a mutant.</summary>
    private static DmlGuardOptions EffectiveDmlGuards(DmlGuardOptions requested)
    {
#if DEBUG
        if (CompilerTestSeams.Current?.DmlGuardMutator is { } mutate)
        {
            return mutate(requested);
        }
#endif
        return requested;
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

        // The engine limit is a typed rejection of the governed path (the parser's own guard throws ArgumentOutOfRangeException).
        if (sql.Length > _engine.MaxQueryLength)
        {
            throw new SqlLimitExceededException(SqlLimitKind.QueryLength, request.TargetDialect, sql.Length, _engine.MaxQueryLength);
        }

        // 2. cache: a hit still validates every table dependency and rebinds the current values (INV-12)
        string rawSql = sql.ToString();
        string keyMaterial = CompileCacheKey.Material(request, rawSql, caps, _engine.MaxQueryLength);
        var policy = request.Policy;
        var guards = EffectiveDmlGuards(policy.Dml);
        var typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps) { Dml = guards };

        string keyHash = CompiledSqlTemplateCache.HashOf(keyMaterial);
        var template = _cache.Find(keyMaterial, keyHash);
        if (template is not null && TryRehydrate(template, typed))
        {
            _cache.CountHit();
            return template.Rebind(typed.PolicyValues);
        }

        _cache.CountMiss();
        if (template is not null)
        {
            // A rehydration attempt may have added values; start clean. A plain miss reuses the context (CR-ADG-12).
            typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps) { Dml = guards };
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

        // 6. typed security injection; user literals first learn the catalog type of the column they are compared with (SEC-ADG-16 item 2)
        Pass("inject");
        var typedLiterals = (SqlStatement)new LiteralColumnTypeAnnotator(policy.Catalog) { CancellationToken = token }.Visit(simplified);
        var secured = (SqlStatement)new AstSecurityVisitor(BuildOptions(request, typed), typed)
        {
            CancellationToken = token
        }.Visit(typedLiterals);
        CheckBudget(request, clock, token);

#if DEBUG
        if (CompilerTestSeams.Current?.FaultyInjector is { } faultyInjector)
        {
            secured = (SqlStatement)faultyInjector(secured);
        }
#endif

        // 7. production coverage proof (does not trust the injector)
        Pass("verify");
#if DEBUG
        var applied = CompilerTestSeams.IsDisabled(SecurityControl.CoverageVerifier)
            ? System.Collections.Immutable.ImmutableArray<SecurityPredicateId>.Empty
            : typed.CreateVerifier(request.TargetDialect).Verify(secured, token);
#else
        var applied = typed.CreateVerifier(request.TargetDialect).Verify(secured, token);
#endif

        // 8. dialect capabilities
        Pass("capabilities");
        DialectCapabilityValidator.Validate(secured, caps);

        // 9. emit (+ bind limit and emitted-text checker inside)
        Pass("emit");
        var generator = SqlDialectGeneratorFactory.GetGenerator(request.TargetDialect);
        var parameterSource = new ParameterSource(typed.PolicyValues, ClientNoValues);
        var compiled = (generator is SqlDialectGeneratorBase capabilityAware
                ? capabilityAware.Generate(secured, parameterSource, caps, token)
                : generator.Generate(secured, parameterSource, token))
            with { AppliedPredicates = applied, ExpectedAffectedRows = Ast.Security.InsertCheckOption.ExpectedRows(secured) };

        // 10. output budget
        long limit = (long)request.MaxExpansionFactor * Math.Max(rawSql.Length, MinimumInputForExpansion);
        if (compiled.Sql.Length > limit)
        {
            throw new SqlLimitExceededException(SqlLimitKind.PolicyExpansionFactor, request.TargetDialect, compiled.Sql.Length, limit);
        }

        CheckBudget(request, clock, token);

        // 11. only verified output is cached, value-free
        _cache.Add(CompiledSqlTemplate.From(keyMaterial, compiled, typed.Dependencies), keyHash);
        return compiled;
    }

    /// <summary>
    /// CR-ADG-02 / INV-1 / plan 3.6: the allowlist is the dialect's function map. A caller list can only narrow it; a function
    /// without a rule is rejected, never passed through. <c>null</c> means "the dialect map", not "everything not denied".
    /// </summary>
    private static IReadOnlySet<string> EffectiveFunctions(CompileRequest request, DialectCapabilities caps)
    {
        var map = request.TranslateTrinoDateFunctions ? caps.Functions.Names : SqlFunctionAllowlists.WithoutDateTranslation(caps.Dialect, caps.Functions.Names);
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

    private static AstSecurityVisitorOptions BuildOptions(CompileRequest request, TypedPolicyContext typed)
    {
        var guards = typed.Dml;
        return new AstSecurityVisitorOptions
        {
            MaskedColumns = new CatalogMaskLookup(typed.Catalog, typed.Masks),
            TableColumns = name => typed.Catalog.Resolve(ParseName(name))?.Columns.Select(c => c.Name).ToList(),
            TablesWithMaskedColumns = new HashSet<string>(request.Policy.TablesWithMaskedColumns, StringComparer.OrdinalIgnoreCase),
            EnforcedMaxRows = request.EnforcedMaxRows,
            TablesWithConsentRowFilter = new HashSet<string>(request.Policy.TablesWithConsentRowFilter, StringComparer.OrdinalIgnoreCase),
            RejectMaskedColumnsInPredicates = true,
            // DML guards of the request (DmlGuardOptions.Strict by default); the coverage verifier proves what they require.
            EnforceWithCheckOption = guards.EnforceWithCheckOption,
            RequireTenantColumnInInsert = guards.RequireTenantColumnInInsert,
            DisallowTenantColumnModificationInUpdate = guards.DisallowTenantColumnModificationInUpdate,
            RejectUnfilteredDml = guards.RejectUnfilteredDml,
            RejectMaskedColumnsInDml = guards.RejectMaskedColumnsInDml,
            RejectConsentFilteredInsert = guards.RejectConsentFilteredInsert,
            RejectWholeRowReferencesInDml = guards.RejectWholeRowReferencesInDml,
            RejectPolicyColumnAssignment = guards.RejectPolicyColumnAssignment
        };
    }

    private static SqlQualifiedName ParseName(string normalized)
    {
        int parts = 1;
        foreach (char c in normalized)
        {
            if (c == '.') parts++;
        }

        // The same parts as normalized.Split('.'), without the intermediate array, iterator and list.
        var identifiers = new SqlIdentifier[parts];
        int start = 0;
        for (int i = 0; i < parts; i++)
        {
            int end = i == parts - 1 ? normalized.Length : normalized.IndexOf('.', start);
            identifiers[i] = new SqlIdentifier(normalized.Substring(start, end - start));
            start = end + 1;
        }

        return new SqlQualifiedName(identifiers);
    }

    /// <summary>Lets the masked-column predicate guards ask the typed mask provider; it never renders SQL.</summary>
    private sealed class CatalogMaskLookup : IMaskedColumnLookup
    {
        private readonly ITableCatalog _catalog;
        private readonly IColumnMaskProvider _masks;
        private Dictionary<string, SqlQualifiedName>? _names;   // one guard per compile: parsed table names are reused for every column check

        public CatalogMaskLookup(ITableCatalog catalog, IColumnMaskProvider masks)
        {
            _catalog = catalog;
            _masks = masks;
        }

        public bool HasMask(string tableName, string columnName)
        {
            _names ??= new Dictionary<string, SqlQualifiedName>(StringComparer.Ordinal);
            if (!_names.TryGetValue(tableName, out var name))
            {
                name = ParseName(tableName);
                _names[tableName] = name;
            }

            var entry = _catalog.Resolve(name);
            return entry is not null && _masks.HasMask(entry.Identity, columnName);
        }
    }
}
