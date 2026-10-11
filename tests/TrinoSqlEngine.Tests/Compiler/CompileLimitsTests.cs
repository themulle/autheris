using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-14 (budget knobs are bounded) and CR-ADG-15 (the Databricks token guard is forced inside the compiler).</summary>
public class CompileLimitsTests
{
    private readonly FastSqlEngine _engine = new();

    private CompileRequest Request(TargetSqlDialect dialect = TargetSqlDialect.SqlServer, SqlTokenSecurityOptions? guards = null) => new()
    {
        TargetDialect = dialect,
        TokenGuards = guards ?? SqlTokenSecurityOptions.Strict,
        AllowExperimentalDialect = true,
        Policy = new GovernancePolicy
        {
            RowFilters = new DictPolicyProvider(),
            Masks = new DictMaskProvider(),
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    };

    private CompiledSql Compile(CompileRequest request, string sql = "SELECT id FROM orders") =>
        _engine.Compile(sql.AsMemory(), request, CancellationToken.None);

    // CR-ADG-41: a template compiled under a higher engine limit is never served after the limit is lowered.
    [Fact]
    public void ALoweredMaxQueryLength_IsNeverBypassedByAWarmCacheEntry()
    {
        string sql = "SELECT id FROM orders WHERE status = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'";
        Compile(Request(), sql);
        Compile(Request(), sql);
        Assert.Equal(1, _engine.CompileCache.Stats.Hits);

        _engine.MaxQueryLength = sql.Length - 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => Compile(Request(), sql));
        Assert.Equal(1, _engine.CompileCache.Stats.Hits);   // no further hit

        _engine.MaxQueryLength = 65_536;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeout_IsRejected(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compile(Request() with { CompileTimeout = TimeSpan.FromTicks(ticks) }));
    }

    [Fact]
    public void InfiniteTimeout_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compile(Request() with { CompileTimeout = Timeout.InfiniteTimeSpan }));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveExpansionFactor_IsRejected(int factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compile(Request() with { MaxExpansionFactor = factor }));
    }

    [Fact]
    public void Timeout_AndExpansionFactor_AreClampedToTheMaximum()
    {
        var normalized = CompileLimits.Normalize(Request() with { CompileTimeout = TimeSpan.FromDays(1), MaxExpansionFactor = int.MaxValue });
        Assert.Equal(CompileLimits.MaxCompileTimeout, normalized.CompileTimeout);
        Assert.Equal(CompileLimits.MaxExpansionFactorLimit, normalized.MaxExpansionFactor);

        // Values inside the bounds are untouched (same instance, no allocation).
        var request = Request() with { CompileTimeout = TimeSpan.FromSeconds(3), MaxExpansionFactor = 10 };
        Assert.Same(request, CompileLimits.Normalize(request));

        // And a clamped request still compiles.
        Assert.NotEmpty(Compile(Request() with { CompileTimeout = TimeSpan.FromDays(1), MaxExpansionFactor = int.MaxValue }).Sql);
    }

    [Fact]
    public void Databricks_VariableSubstitutionGuard_IsForcedEvenWhenTheCallerPassesNone()
    {
        const string sql = "SELECT id FROM orders WHERE status = '${spark.sql.shuffle.partitions}'";
        var none = Request(TargetSqlDialect.Databricks, SqlTokenSecurityOptions.None);

        var ex = Assert.ThrowsAny<Exception>(() => Compile(none, sql));
        Assert.Contains("Variable substitution", ex.Message, StringComparison.Ordinal);
        Assert.True(CompileLimits.Normalize(none).TokenGuards.RejectVariableSubstitutionSequences);
    }

    [Fact]
    public void OtherDialects_DoNotGetTheDatabricksGuard()
    {
        var none = Request(TargetSqlDialect.SqlServer, SqlTokenSecurityOptions.None);
        Assert.False(CompileLimits.Normalize(none).TokenGuards.RejectVariableSubstitutionSequences);
        // The text is an ordinary string literal on SQL Server and is bound, never executed or substituted.
        var compiled = Compile(none, "SELECT id FROM orders WHERE status = '${x}'");
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "${x}"));
    }
}
