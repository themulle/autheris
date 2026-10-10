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
        using var activity = CompilerTelemetry.Source.StartActivity("sql.compile");
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.CompileTimeout > TimeSpan.Zero && request.CompileTimeout != Timeout.InfiniteTimeSpan)
        {
            budget.CancelAfter(request.CompileTimeout);
        }

        try
        {
            var compiled = CompileCore(sql, request, clock, budget.Token);
            activity?.SetTag("sql.target_dialect", compiled.Dialect.ToString());
            activity?.SetTag("sql.statement_class", compiled.StatementClass.ToString());
            activity?.SetTag("sql.compiler_version", compiled.CompilerVersion);
            activity?.SetTag("sql.bind_count", compiled.Parameters.Length);
            return compiled;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var ex = new SqlLimitExceededException(SqlLimitKind.CompileTime, request.TargetDialect, clock.ElapsedMilliseconds, (long)request.CompileTimeout.TotalMilliseconds);
            CompilerTelemetry.RecordRejected(request.TargetDialect, ex);
            throw ex;
        }
        catch (Exception ex)
        {
            CompilerTelemetry.RecordRejected(request.TargetDialect, ex);
            throw;
        }
    }

    private static void CheckBudget(CompileRequest request, Stopwatch clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.CompileTimeout > TimeSpan.Zero && clock.Elapsed >= request.CompileTimeout)
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

        if (request.Statements != StatementPermissions.ReadOnly)
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, request.Statements.ToString());
        }

        CheckBudget(request, clock, token);

        // 2. cache: a hit still validates every table dependency and rebinds the current values (INV-12)
        string rawSql = sql.ToString();
        string keyMaterial = CompileCacheKey.Material(request, rawSql, caps);
        var policy = request.Policy;
        var typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps);

        var template = _cache.Find(keyMaterial);
        if (template is not null && TryRehydrate(template, typed))
        {
            _cache.CountHit();
            return template.Rebind(typed.PolicyValues);
        }

        _cache.CountMiss();
        typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps);

        // 3. token guards + parse
        var (tree, _) = _engine.Parse(sql, request.TokenGuards, token);
        EnsureQueryStatement(tree);
        CheckBudget(request, clock, token);

        // 4. build + validate the user tree
        var builderOptions = new AstBuilderOptions
        {
            EnforceReadOnlyQueries = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = request.AllowedFunctions,
            AllowedTableFunctions = request.AllowedTableFunctions,
            TranslateTrinoDateFunctions = request.TranslateTrinoDateFunctions,
            RejectTimeTravelQueries = true
        };
        var ast = new SqlAstBuilder(builderOptions).BuildStatement(tree);
        new AstValidationVisitor { CancellationToken = token }.Validate(ast);

        // 5. simplify the USER tree only: injected predicates do not exist yet (INV-3 by construction)
        var simplified = (SqlStatement)new AstSimplificationVisitor { CancellationToken = token }.Visit(ast);
        CheckBudget(request, clock, token);

        // 6. typed security injection
        var secured = (SqlStatement)new AstSecurityVisitor(BuildOptions(request, typed), _engine, typed)
        {
            CancellationToken = token
        }.Visit(simplified);
        CheckBudget(request, clock, token);

        // 7. production coverage proof (does not trust the injector)
        var applied = typed.CreateVerifier(request.TargetDialect).Verify(secured, token);

        // 8. dialect capabilities
        DialectCapabilityValidator.Validate(secured, caps);

        // 9. emit (+ bind limit and emitted-text checker inside)
        var generator = SqlDialectGeneratorFactory.GetGenerator(request.TargetDialect);
        var compiled = generator.Generate(secured, new ParameterSource(typed.PolicyValues.ToFrozenDictionary(StringComparer.Ordinal), new Dictionary<string, object?>()), token)
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

    private static void EnsureQueryStatement(SqlBaseParser.SingleStatementContext tree)
    {
        switch (tree.statement())
        {
            case SqlBaseParser.StatementDefaultContext:
                return;
            case SqlBaseParser.InsertIntoContext:
                throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, "Insert");
            case SqlBaseParser.UpdateContext:
                throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, "Update");
            case SqlBaseParser.DeleteContext:
                throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, "Delete");
            case SqlBaseParser.MergeContext:
                throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, "Merge");
            default:
                throw new SecurityException("Only read queries are permitted by the governed SQL compiler.");
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
            RejectMaskedColumnsInDml = true
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
