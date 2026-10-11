#if DEBUG
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// CR-ADG-04 and CR-ADG-19: the pipeline must itself call the coverage verifier and the emitted-text checker, and must never
/// cache unverified output. The Debug-only seam injects a faulty injector or emitter; a mutant that skips either proof, or caches
/// before verification, fails these tests. The same seam cancels from inside every pass.
/// </summary>
public class PipelineSeamTests
{
    private static readonly string[] Passes = { "parse", "build", "validate", "simplify", "inject", "verify", "capabilities", "emit" };

    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public static IEnumerable<object[]> Dialects() => new[]
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks
    }.Select(d => new object[] { d });

    public static IEnumerable<object[]> PassNames() => Passes.Select(p => new object[] { p });

    private CompileRequest Request(TargetSqlDialect dialect) => new()
    {
        TargetDialect = dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        AllowExperimentalDialect = true,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    };

    private CompiledSql Compile(TargetSqlDialect dialect, string sql = "SELECT id FROM orders", CancellationToken ct = default) =>
        _engine.Compile(sql.AsMemory(), Request(dialect), ct);

    /// <summary>The unsecured statement a faulty injector would produce: the physical table, no tenant predicate.</summary>
    private static SqlStatement Unsecured() =>
        new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Orders", true) }), null),
                null, null, null), null, null);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void FaultyInjector_DroppingTheTenantPredicate_IsRejectedByTheVerifier(TargetSqlDialect dialect)
    {
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyInjector = _ => Unsecured() });
        Assert.Throws<SecurityCoverageException>(() => Compile(dialect));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void FailedVerification_NeverInsertsACacheEntry(TargetSqlDialect dialect)
    {
        using (CompilerTestSeams.Use(new CompilerTestSeams { FaultyInjector = _ => Unsecured() }))
        {
            Assert.Throws<SecurityCoverageException>(() => Compile(dialect));
        }

        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
        Assert.Empty(_engine.CompileCache.Snapshot());

        // Without the fault the same request compiles, and only then is a template cached.
        Assert.NotEmpty(Compile(dialect).Sql);
        Assert.Equal(1, _engine.CompileCache.Stats.Entries);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void FaultyEmitter_AddingALiteral_IsRejectedByTheEmittedTextChecker(TargetSqlDialect dialect)
    {
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyEmitter = sql => sql.Replace("SELECT", "SELECT 'smuggled',", StringComparison.Ordinal) });
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Compile(dialect));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void FaultyEmitter_AddingAStatementSeparator_IsRejectedByTheEmittedTextChecker(TargetSqlDialect dialect)
    {
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyEmitter = sql => sql + "; SELECT 1" });
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Compile(dialect));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void FaultyEmitter_AddingAnUnboundNumber_IsRejectedByTheEmittedTextChecker(TargetSqlDialect dialect)
    {
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyEmitter = sql => sql + " WHERE 1 = 1 OR 42 = 42" });
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Compile(dialect));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryPass_ReportsItselfInOrder(TargetSqlDialect dialect)
    {
        var seen = new List<string>();
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { OnPass = seen.Add });
        Compile(dialect);
        Assert.Equal(Passes, seen);
    }

    [Theory]
    [MemberData(nameof(PassNames))]
    public void Compile_CancelledInsideEveryPass_NoCacheEntry_NoPartialSql(string pass)
    {
        using var cts = new CancellationTokenSource();
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams
        {
            OnPass = name =>
            {
                if (name == pass) cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => Compile(TargetSqlDialect.SqlServer, ct: cts.Token));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
    }

    [Fact]
    public void TheSeam_IsNotInstalledByDefault()
    {
        Assert.Null(CompilerTestSeams.Current);
    }
}
#endif
