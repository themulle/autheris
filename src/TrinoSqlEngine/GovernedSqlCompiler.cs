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

    /// <summary>Test hook: observes (and may cancel from) the start of a pass. A no-op in production.</summary>
    private static void Pass(string name) => CompilerHooks.Current.OnPass(name);

    /// <summary>The DML guards the pipeline applies: the validated request guards (strict), except when test hooks run a mutant.</summary>
    private static DmlGuardOptions EffectiveDmlGuards(DmlGuardOptions requested) => CompilerHooks.Current.InterceptDmlGuards(requested);

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

        request = ValidateKeysetRequest(request);

        // 2. cache: a hit still validates every table dependency and rebinds the current values (INV-12)
        // The key stays in the thread-local builder on the hot (hit) path; the raw text and the key string exist only after a miss.
        var keyBuilder = CompileCacheKey.Build(request, sql.Span, caps, _engine.MaxQueryLength);
        var policy = request.Policy;
        var guards = EffectiveDmlGuards(policy.Dml);
        var typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps) { Dml = guards, Write = policy.Write, PerCellColumns = policy.PerCellColumns };

        string keyHash = CompiledSqlTemplateCache.HashOf(keyBuilder);
        var template = _cache.Find(keyBuilder, keyHash);
        if (template is not null && TryRehydrate(template, typed))
        {
            CompileCacheKey.ReleaseBuilder(keyBuilder);
            _cache.CountHit();
            return template.Rebind(typed.PolicyValues, request.PageOffset, request.MaxAllowedOffset, request.KeysetSeek?.Values);
        }

        string keyMaterial = keyBuilder.ToString();
        CompileCacheKey.ReleaseBuilder(keyBuilder);
        string rawSql = sql.ToString();
        _cache.CountMiss();
        if (template is not null)
        {
            // A rehydration attempt may have added values; start clean. A plain miss reuses the context (CR-ADG-12).
            typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps) { Dml = guards, Write = policy.Write, PerCellColumns = policy.PerCellColumns };
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

        // 4a. issue #277: a recursive WITH inside DML is rejected whatever the request allows (read-only scope). The gate runs
        // before the security visitor, so no shadow check, catalog lookup or injection precedes it.
        if (statementClass != SqlStatementClass.Select && ContainsRecursiveWith(ast))
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "WITH RECURSIVE");
        }

        // 4b. time zone conversion: every call is checked on the USER tree, before simplification (no folding can turn a non-literal
        // into a literal) and before injection (policy and mask expressions never contain the function). A statement whose text does not spell the
        // function name has no call (the builder refuses delimited function names, so the name of a call is always spelled in the text) and skips the
        // pass; the emitter is then told that nothing was validated, so a call that appeared anyway could not be emitted (BC-130-12).
        bool timeZoneCallsValidated = false;
        if (rawSql.Contains(TimeZoneCallValidator.FunctionName, StringComparison.OrdinalIgnoreCase))
        {
            Pass("timezone");
            new TimeZoneCallValidator(policy.Catalog, new TimeZoneValidationOptions(
                request.TargetDialect, caps.SupportsTimeZoneConversion, request.TimeZoneConversionAvailable, policy.Masks, policy.TablesWithMaskedColumns,
                request.ClientLists, Federated: false)) { CancellationToken = token }.Validate(ast);
            timeZoneCallsValidated = true;
        }

        // 5. simplify the USER tree only: injected predicates do not exist yet (INV-3 by construction)
        Pass("simplify");
        var simplified = (SqlStatement)new AstSimplificationVisitor { CancellationToken = token }.Visit(ast);
        CheckBudget(request, clock, token);

        // 5b. client lists: expanded on the user tree BEFORE the security visitor, so the DML guard judges the
        // expanded predicate (an empty NOT IN list is the constant 1 = 1). The mutation control moves the pass behind it.
        ClientListExpander? expander = null;
        bool listsBeforeGuards = !CompilerHooks.IsDisabled(SecurityControl.ClientListExpandsBeforeGuards);
        if (!request.ClientLists.IsEmpty)
        {
            expander = new ClientListExpander(request.ClientLists, caps) { CancellationToken = token };
            if (listsBeforeGuards)
            {
                Pass("lists");
                simplified = (SqlStatement)expander.Visit(simplified);
                CheckBudget(request, clock, token);
            }
        }

        // 5c. keyset continuation: the shape of the user tree decides eligibility; the seek predicate joins the user WHERE before the
        // security visitor, so row filters, mask guards and the coverage proof all judge the final tree.
        var keyset = AnalyzeKeyset(request, caps, typed, statementClass, simplified);
        simplified = keyset.Tree;

        // 6. typed security injection; user literals first learn the catalog type of the column they are compared with (16 item 2)
        Pass("inject");
        var typedLiterals = (SqlStatement)new LiteralColumnTypeAnnotator(policy.Catalog) { CancellationToken = token }.Visit(simplified);
        typedLiterals = (SqlStatement)new IntegralOperandClassifier(policy.Catalog) { CancellationToken = token }.Visit(typedLiterals);
        var visitor = new AstSecurityVisitor(BuildOptions(request, typed, _engine.MaxQueryLength), typed)
        {
            KeysetKeyColumns = keyset.Shape.Columns,
            CancellationToken = token
        };
        SqlStatement secured;
        try
        {
            secured = (SqlStatement)visitor.Visit(typedLiterals);
        }
        catch (MaskedColumnPredicateException) when (request.KeysetSeek is not null && keyset.Reason is null)
        {
            // A key that is masked now cannot be compared: the continuation no longer fits the statement.
            throw new KeysetSeekRejectedException();
        }

        bool evaluationFenced = visitor.RowPolicyApplied
            && caps.HasEvaluationFence
            && Ast.Security.RowPolicyErrorGuard.AreAllPolicyTablesFenced(secured);
        IReadOnlyDictionary<string, BindFamily>? bindFamilies = null;
        if (visitor.RowPolicyApplied && !CompilerHooks.IsDisabled(SecurityControl.RowPolicyExpressionAllowlist))
        {
            // Details: private advisory. The user tree (without any injected predicate) is checked.
            bindFamilies = Ast.Security.RowPolicyErrorGuard.Enforce(typedLiterals, policy.Catalog, evaluationFenced);
        }

        CheckBudget(request, clock, token);

        var (keysetKey, keysetUnavailable) = ResolveKeysetKey(request, keyset, visitor);

        secured = CompilerHooks.Current.InterceptInjected(secured);

        // 6b. optional filters (@p IS NULL OR X) are reduced by the null-ness of the parameter. Every guard above judged the unreduced
        // tree, so the verdict is independent of the values; the coverage proof below runs on the reduced tree. SELECT only.
        if (secured is SelectStatement)
        {
            Pass("optional-filters");
            secured = (SqlStatement)new OptionalFilterReducer(request.ClientNulls) { CancellationToken = token }.Visit(secured);
            CheckBudget(request, clock, token);
        }

        if (expander is not null && !listsBeforeGuards)
        {
            Pass("lists");
            secured = (SqlStatement)expander.Visit(secured);
        }

        // 7. production coverage proof (does not trust the injector)
        Pass("verify");
        var applied = CompilerHooks.IsDisabled(SecurityControl.CoverageVerifier)
            ? System.Collections.Immutable.ImmutableArray<SecurityPredicateId>.Empty
            : typed.CreateVerifier(request.TargetDialect, preExecutionRowCheck: visitor.PreExecutionRowCheck).Verify(secured, token);

        // 8. dialect capabilities
        Pass("capabilities");
        DialectCapabilityValidator.Validate(secured, caps);

        // 9. emit (+ bind limit and emitted-text checker inside)
        Pass("emit");
        var generator = SqlDialectGeneratorFactory.GetGenerator(request.TargetDialect);
        var parameterSource = new ParameterSource(typed.PolicyValues, ClientNoValues);
        var compiled = (generator is SqlDialectGeneratorBase capabilityAware
                ? capabilityAware.Generate(secured, parameterSource, caps, token, timeZoneCallsValidated: timeZoneCallsValidated, maxRecursionDepth: request.MaxRecursionDepth)
                : generator.Generate(secured, parameterSource, token))
            with
            {
                AppliedPredicates = applied,
                ExpectedAffectedRows = Ast.Security.InsertCheckOption.ExpectedRows(secured),
                OutputColumnMasks = visitor.OutputColumnMasks,
                KeysetKey = keysetKey,
                KeysetUnavailable = keysetUnavailable,
                IsPreBoundedDml = visitor.IsPreBoundedDml,
                PreBoundedMaxRows = visitor.PreBoundedMaxRows,
                PreExecutionRowCheck = visitor.PreExecutionRowCheck,
                PreExecutionPolicyValues = visitor.PreExecutionRowCheck is not null
                    ? System.Collections.Immutable.ImmutableDictionary.ToImmutableDictionary(typed.PolicyValues, StringComparer.Ordinal)
                    : System.Collections.Immutable.ImmutableDictionary<string, PolicyValue>.Empty
            };

        // 10. output budget
        // The marker text of the lists that were actually expanded is not policy expansion: it is subtracted from the
        // emitted length, derived from the expander counter and never from the request shapes.
        long limit = (long)request.MaxExpansionFactor * Math.Max(rawSql.Length, CompileLimits.MinimumInputForExpansion);
        long emitted = compiled.Sql.Length - (expander?.ExpandedItems ?? 0) * ClientListExpander.MarkerAllowance;
        if (emitted > limit)
        {
            throw new SqlLimitExceededException(SqlLimitKind.PolicyExpansionFactor, request.TargetDialect, compiled.Sql.Length, limit);
        }

        // Enforce an absolute emitted length ceiling proportional to the engine query cap so dialect function wrappers cannot amplify unboundedly.
        long hardCap = 4L * _engine.MaxQueryLength;
        if (compiled.Sql.Length > hardCap)
        {
            throw new SqlLimitExceededException(SqlLimitKind.EmittedSqlLength, request.TargetDialect, compiled.Sql.Length, hardCap);
        }

        CheckBudget(request, clock, token);

        // 10b. client binds that meet a typed column under a row policy carry the family the binder checks the value against
        if (bindFamilies is { Count: > 0 })
        {
            var patched = compiled.Parameters.ToBuilder();
            for (int i = 0; i < patched.Count; i++)
            {
                var parameter = patched[i];
                if (parameter.Origin == ParameterOrigin.ClientNamed && parameter.SourceName is { } sourceName && bindFamilies.TryGetValue(sourceName, out var family))
                {
                    patched[i] = parameter with { RequiredFamily = family };
                }
            }

            compiled = compiled with { Parameters = patched.MoveToImmutable() };
        }

        // 11. only verified output is cached, value-free
        var templateToCache = CompiledSqlTemplate.From(keyMaterial, compiled, typed.Dependencies);
        _cache.Add(templateToCache, keyHash);
        if (request.BindPageOffset || request.KeysetSeek is not null)
        {
            return templateToCache.Rebind(typed.PolicyValues, request.PageOffset, request.MaxAllowedOffset, request.KeysetSeek?.Values);
        }
        return compiled;
    }

    private readonly record struct KeysetAnalysis(SqlStatement Tree, KeysetShape Shape, string? Reason, string ShapeHash);

    /// <summary>
    /// A seek is accepted only as a server-issued continuation of a keyset candidate read: never without the candidate, never with a page
    /// offset, never with fewer than one or more than eight values, never with a missing value. Fails closed before the cache is consulted.
    /// </summary>
    private static CompileRequest ValidateKeysetRequest(CompileRequest request)
    {
        if (request.Keyset is { KeyColumns.IsDefault: true })
        {
            request = request with { Keyset = new KeysetCandidate(System.Collections.Immutable.ImmutableArray<string>.Empty) };
        }

        if (request.KeysetSeek is not { } seek)
        {
            return request;
        }

        if (request.Keyset is null || request.BindPageOffset || request.PageOffset.HasValue
            || string.IsNullOrEmpty(seek.ShapeHash) || seek.Values.IsDefaultOrEmpty || seek.Values.Length > KeysetLimits.MaxKeyColumns)
        {
            throw new KeysetSeekRejectedException();
        }

        foreach (var value in seek.Values)
        {
            if (value is null)
            {
                throw new KeysetSeekRejectedException();
            }

            SqlParameterType type;
            try
            {
                type = DbCommandCompiledSqlBinder.InferType(value);
            }
            catch (SecurityException)
            {
                throw new KeysetSeekRejectedException();
            }

            if (type is SqlParameterType.Null or SqlParameterType.Binary or SqlParameterType.Boolean or SqlParameterType.Double or SqlParameterType.Time)
            {
                throw new KeysetSeekRejectedException();
            }
        }

        return request;
    }

    private static KeysetAnalysis AnalyzeKeyset(CompileRequest request, DialectCapabilities caps, TypedPolicyContext typed, SqlStatementClass statementClass, SqlStatement tree)
    {
        if (request.Keyset is not { } candidate)
        {
            return new KeysetAnalysis(tree, default, null, string.Empty);
        }

        Pass("keyset");
        KeysetShape shape = default;
        string? reason;
        string hash = string.Empty;
        if (statementClass != SqlStatementClass.Select)
        {
            reason = KeysetUnavailableReasons.Shape;
        }
        else
        {
            shape = KeysetShapeAnalyzer.Analyze(tree, candidate);
            reason = shape.Reason;
        }

        System.Collections.Immutable.ImmutableArray<string> catalogTypes = default;
        if (reason is null)
        {
            (reason, catalogTypes) = ResolveKeyTypes(request, typed, shape);
        }

        if (reason is null)
        {
            hash = KeysetShapeHash.Compute(shape.Columns, shape.Descending, catalogTypes);
        }

        if (request.KeysetSeek is not { } seek)
        {
            return new KeysetAnalysis(tree, shape, reason, hash);
        }

        if (reason is not null || !string.Equals(seek.ShapeHash, hash, StringComparison.Ordinal) || seek.Values.Length != shape.Columns.Length)
        {
            throw new KeysetSeekRejectedException();
        }

        var types = System.Collections.Immutable.ImmutableArray.CreateBuilder<SqlParameterType>(seek.Values.Length);
        foreach (var value in seek.Values)
        {
            types.Add(DbCommandCompiledSqlBinder.InferType(value));
        }

        var injected = KeysetSeekInjector.Inject(tree, shape, types.MoveToImmutable(), catalogTypes);
        return new KeysetAnalysis(injected, shape, null, hash);
    }

    private static (string? Reason, System.Collections.Immutable.ImmutableArray<string> Types) ResolveKeyTypes(CompileRequest request, TypedPolicyContext typed, KeysetShape shape)
    {
        if (request.TargetDialect is not (TargetSqlDialect.PostgreSql or TargetSqlDialect.SqlServer or TargetSqlDialect.DuckDb
            or TargetSqlDialect.Databricks or TargetSqlDialect.Oracle)
            || shape.Table is null
            || typed.Catalog.Resolve(shape.Table.Name) is not { IsMetricView: false } entry)
        {
            return (KeysetUnavailableReasons.Shape, default);
        }

        var types = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>(shape.Columns.Length);
        foreach (var key in shape.Columns)
        {
            string? dataType = ColumnDataTypeOf(entry, key);
            if (dataType is null)
            {
                return (KeysetUnavailableReasons.NotPrimaryKey, default);
            }

            // Oracle's NLS_SORT and NLS_COMP can make ORDER BY and the comparison operators disagree for text.
            if (request.TargetDialect == TargetSqlDialect.Oracle
                && ColumnBindAdapter.Classify(dataType) is ColumnTypeClass.AnsiText or ColumnTypeClass.UnicodeText)
            {
                return (KeysetUnavailableReasons.Shape, default);
            }

            types.Add(dataType);
        }

        return (null, types.MoveToImmutable());
    }

    /// <summary>The keyset key of an eligible statement, or the reason it is unavailable. With a seek, any reason is a rejection.</summary>
    private static (KeysetKeyInfo? Key, string? Unavailable) ResolveKeysetKey(CompileRequest request, KeysetAnalysis keyset, AstSecurityVisitor visitor)
    {
        if (request.Keyset is null)
        {
            return (null, null);
        }

        string? reason = keyset.Reason;
        KeysetKeyInfo? key = null;
        if (reason is null)
        {
            var ordinals = visitor.RootKeysetOrdinals;
            var masks = visitor.OutputColumnMasks;
            if (ordinals.IsDefaultOrEmpty || ordinals.Length != keyset.Shape.Columns.Length)
            {
                reason = KeysetUnavailableReasons.KeyNotProjected;
            }
            else
            {
                foreach (int ordinal in ordinals)
                {
                    if (ordinal < 0 || ordinal >= masks.Length)
                    {
                        reason = KeysetUnavailableReasons.KeyNotProjected;
                        break;
                    }

                    if (masks[ordinal])
                    {
                        reason = KeysetUnavailableReasons.KeyMasked;
                        break;
                    }
                }
            }

            if (reason is null)
            {
                key = new KeysetKeyInfo(keyset.Shape.Columns, keyset.Shape.Descending, ordinals, keyset.ShapeHash);
            }
        }

        if (reason is not null && request.KeysetSeek is not null)
        {
            throw new KeysetSeekRejectedException();
        }

        return (key, reason);
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

    private static bool ContainsRecursiveWith(SqlNode root)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is WithClause { IsRecursive: true })
            {
                return true;
            }

            foreach (var child in node.EnumerateChildren())
            {
                stack.Push(child);
            }
        }

        return false;
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

    /// <summary>The column names of a catalog entry as one exactly sized array (no iterator, no list growth per call).</summary>
    private static IReadOnlyList<string>? ColumnNames(TableCatalogEntry? entry)
    {
        if (entry is null) return null;
        var columns = entry.Columns;
        var names = new string[columns.Length];
        for (int i = 0; i < names.Length; i++) names[i] = columns[i].Name;
        return names;
    }

    private static string? ColumnDataTypeOf(TableCatalogEntry? entry, string column)
    {
        if (entry is null) return null;
        foreach (var candidate in entry.Columns)
        {
            if (candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) return candidate.DataType;
        }

        return null;
    }

    private static AstSecurityVisitorOptions BuildOptions(CompileRequest request, TypedPolicyContext typed, int maxQueryLength)
    {
        var guards = typed.Dml;
        return new AstSecurityVisitorOptions
        {
            MaskedColumns = new CatalogMaskLookup(typed.Catalog, typed.Masks),
            TableColumns = name => ColumnNames(typed.Catalog.Resolve(ParseName(name))),
            ColumnDataType = (table, column) => ColumnDataTypeOf(typed.Catalog.Resolve(ParseName(table)), column),
            TablesWithMaskedColumns = CaseInsensitive(request.Policy.TablesWithMaskedColumns),
            EnforcedMaxRows = request.EnforcedMaxRows,
            AllowRecursiveCte = request.AllowRecursiveCte,
            BindPageOffset = request.BindPageOffset,
            GatewayLikePatternParameters = request.GatewayLikePatternParameters,
            TargetDialect = request.TargetDialect,
            MaxQueryLength = maxQueryLength,
            TablesWithConsentRowFilter = CaseInsensitive(request.Policy.TablesWithConsentRowFilter),
            WritePermit = request.Policy.Write,
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

    /// <summary>
    /// The table-name sets are matched case-insensitively. The set that the policy factory builds already is (a frozen, case-insensitive
    /// set is passed on as it is, with no copy per compile); any other set is copied into a case-insensitive one, as before.
    /// </summary>
    private static IReadOnlySet<string> CaseInsensitive(IReadOnlySet<string> names) => names switch
    {
        { Count: 0 } => names,
        FrozenSet<string> frozen when ReferenceEquals(frozen.Comparer, StringComparer.OrdinalIgnoreCase) => frozen,
        _ => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
    };

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
            int end = i == parts - 1 ? normalized.Length : normalized.IndexOf('.', start, StringComparison.Ordinal);
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
