using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Security;

#if DEBUG
/// <summary>
/// The seam of the mutant that is currently active, so that a probe which needs its own faulty injector or emitter can layer it
/// over the mutant instead of replacing it (WP-X0v). Null when no mutant is active.
/// </summary>
internal static class MutantContext
{
    private static readonly AsyncLocal<CompilerTestSeams?> Active = new();

    public static CompilerTestSeams? Current => Active.Value;

    public static IDisposable Enter(CompilerTestSeams? mutant)
    {
        var previous = Active.Value;
        Active.Value = mutant;
        var inner = mutant is null ? null : CompilerTestSeams.Use(mutant);
        return new Scope(() =>
        {
            inner?.Dispose();
            Active.Value = previous;
        });
    }

    /// <summary>Installs a probe seam on top of the active mutant: the mutant's operator runs first, then the probe's fault.</summary>
    public static IDisposable Overlay(
        Func<SqlStatement, SqlStatement>? injector = null,
        Func<string, string>? emitter = null,
        int? exhaustStackAtGuardCall = null,
        Action<string>? onPass = null)
    {
        var mutant = Active.Value;
        Func<SqlStatement, SqlStatement>? composedInjector = (injector, mutant?.FaultyInjector) switch
        {
            (null, var m) => m,
            (var p, null) => p,
            (var p, var m) => s => p!(m!(s))
        };
        Func<string, string>? composedEmitter = (emitter, mutant?.FaultyEmitter) switch
        {
            (null, var m) => m,
            (var p, null) => p,
            (var p, var m) => s => p!(m!(s))
        };
        return CompilerTestSeams.Use(new CompilerTestSeams
        {
            DisabledControls = mutant?.DisabledControls,
            OmittedCacheKeyFields = mutant?.OmittedCacheKeyFields,
            DmlGuardMutator = mutant?.DmlGuardMutator,
            FaultyInjector = composedInjector,
            FaultyEmitter = composedEmitter,
            ExhaustStackAtGuardCall = exhaustStackAtGuardCall ?? mutant?.ExhaustStackAtGuardCall,
            OnPass = onPass ?? mutant?.OnPass
        });
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

/// <summary>Test-code mutation operators for the injector output (plan M-3: "operators in test code").</summary>
internal sealed class DropScopeRewriter(params SecurityScope[] scopes) : SqlAstRewriter
{
    private readonly HashSet<SecurityScope> _scopes = new(scopes);

    public override SqlNode Visit(SqlNode node) => node switch
    {
        // the injected predicate of the scope is replaced by TRUE: the statement still compiles, it is just no longer secured
        SecurityPredicateExpression p when _scopes.Contains(p.Scope) => new LiteralExpression(true, LiteralType.Boolean),
        // an outer predicate is opaque to the base rewriter; descend so that a policy subquery inside it is reachable
        SecurityPredicateExpression p => p with { Predicate = (Expression)Visit(p.Predicate) },
        _ => base.Visit(node)
    };
}

/// <summary>Replaces every typed mask by the clear column (the wildcard expansion no longer masks).</summary>
internal sealed class UnmaskRewriter : SqlAstRewriter
{
    public override SqlNode Visit(SqlNode node) => node switch
    {
        MaskExpression m => m.Column,
        SecurityPredicateExpression p => p with { Predicate = (Expression)Visit(p.Predicate) },
        _ => base.Visit(node)
    };
}

/// <summary>A MERGE clause type that the compiler does not know (a stand-in for <c>WHEN NOT MATCHED BY SOURCE</c>).</summary>
internal sealed record UnknownMergeClause() : MergeClause((Expression?)null);

internal sealed class AddUnknownMergeClauseRewriter : SqlAstRewriter
{
    public override SqlNode VisitMergeStatement(MergeStatement node)
    {
        var rewritten = (MergeStatement)base.VisitMergeStatement(node);
        return rewritten with { Clauses = rewritten.Clauses.Concat(new MergeClause[] { new UnknownMergeClause() }).ToList() };
    }
}

#endif

/// <summary>One governed compile environment per probe: its own engine (and so its own plan cache), policies and catalog.</summary>
internal sealed class ProbeEnv
{
    public ProbeEnv(TargetSqlDialect dialect)
    {
        Dialect = dialect;
        RowFilters.NoPolicy.Add(Orders);
        RowFilters.NoPolicy.Add(Entitlements);
        RowFilters.NoPolicy.Add(Lookup);
    }

    public TargetSqlDialect Dialect { get; }

    public FastSqlEngine Engine { get; } = new();

    public DictPolicyProvider RowFilters { get; } = new();

    public DictMaskProvider Masks { get; } = new();

    public CompileRequest Request(
        string tenant = "acme",
        SqlTokenSecurityOptions? guards = null,
        StatementPermissions statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        int enforcedMaxRows = 0,
        TargetSqlDialect? dialect = null) => new()
    {
        TargetDialect = dialect ?? Dialect,
        AllowExperimentalDialect = true,   // Databricks stays Experimental until the first green G9 run
        TokenGuards = guards ?? SqlTokenSecurityOptions.Strict,
        Statements = statements,
        EnforcedMaxRows = enforcedMaxRows,
        Policy = new GovernancePolicy
        {
            RowFilters = RowFilters,
            Masks = Masks,
            Catalog = DmlCompileTests.CatalogFor(dialect ?? Dialect),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    public CompiledSql Compile(string sql, string tenant = "acme") =>
        Engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);

    /// <summary>The row policy "Region = EU" on orders (a consent-style filter inside one tenant).</summary>
    public void RegionPolicy(string value = "EU")
    {
        RowFilters.NoPolicy.Remove(Orders);
        RowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new(value, SqlParameterType.String) });
    }

    /// <summary>A policy whose predicate is a correlated EXISTS over another catalog table (SEC-ADG-11 policy subquery).</summary>
    public void PolicySubqueryPolicy()
    {
        static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p, false)).ToList()));
        RowFilters.NoPolicy.Remove(Orders);
        RowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new ExistsExpression(new SelectStatement(null,
                new QuerySpecification(false,
                    new SelectItem[] { new ColumnSelectItem(Col("Entitlements", "Id"), null) },
                    new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", false) }), null),
                    new BinaryExpression(Col("Entitlements", "OrderId"), BinaryOperator.Equal, Col("autheris_target", "Id")),
                    null, null), null, null)),
            new Dictionary<string, PolicyValue>());
    }

    /// <summary>The email column of orders masked with a keyed HMAC (the key is a bound mask parameter).</summary>
    public void MaskEmailHmac() =>
        Masks.Masks[(Orders, "email")] = new MaskSpec(
            MaskKind.Hmac,
            new MaskArguments(HmacKey: new PolicyParameterExpression("m_key", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["m_key"] = new("00ff00ff00ff00ff", SqlParameterType.String) }.ToFrozenDictionary());

    /// <summary>The email column of orders masked with a partial mask (prefix and suffix kept, the rest replaced).</summary>
    public void MaskEmailPartial() =>
        Masks.Masks[(Orders, "email")] = new MaskSpec(
            MaskKind.PartialMask,
            new MaskArguments(
                KeepPrefix: new PolicyParameterExpression("m_p", SqlParameterType.Int32, ParameterOrigin.Mask),
                KeepSuffix: new PolicyParameterExpression("m_s", SqlParameterType.Int32, ParameterOrigin.Mask),
                MaskChar: new PolicyParameterExpression("m_c", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue>
            {
                ["m_p"] = new(1, SqlParameterType.Int32), ["m_s"] = new(1, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            }.ToFrozenDictionary());

    public void MaskEmail() =>
        Masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), System.Collections.Frozen.FrozenDictionary<string, PolicyValue>.Empty);
}

internal static class ProbeAsserts
{
    /// <summary>How many tenant predicates a dialect's template puts around one bound tenant marker (see TenantPredicateFactory).</summary>
    public static int MentionsPerTenantPredicate(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => 3,
        TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb or TargetSqlDialect.Oracle => 2,
        TargetSqlDialect.Databricks => 1,
        _ => throw new NotSupportedException(dialect.ToString())
    };

    /// <summary>
    /// The number of times the bound tenant marker appears in the emitted text. It does not depend on the verifier: every
    /// injected tenant predicate mentions the marker a fixed number of times, so a dropped predicate is visible in the text.
    /// </summary>
    public static int TenantMarkerMentions(CompiledSql compiled)
    {
        var tenant = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Tenant).Select(p => p.Marker).Distinct().ToList();
        if (tenant.Count == 0) return 0;
        Xunit.Assert.Single(tenant);
        return Regex.Count(compiled.Sql, Regex.Escape(tenant[0]) + @"(?![\w$])");
    }

    public static void TenantPredicates(CompiledSql compiled, int expectedPredicates) =>
        Xunit.Assert.Equal(expectedPredicates * MentionsPerTenantPredicate(compiled.Dialect), TenantMarkerMentions(compiled));
}

/// <summary>A command with a settable <c>BindByName</c> property, like ODP.NET's <c>OracleCommand</c>, over an in-memory SQLite command.</summary>
internal sealed class FakeOracleCommand : DbCommand
{
    private readonly SqliteCommand _inner = new();

    public bool BindByName { get; set; }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value; }
    public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }
    public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() => _inner.Cancel();
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
