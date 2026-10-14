using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Security;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// ARC-S-07: every dialect is described by data. A dialect with a capability entry must also have a generator, binder, allowlist,
/// type map and marker style; a dialect without one must fail closed. Known remaining enum-keyed sites (not data driven yet):
/// the IsSqlServer mask guards in AstSecurityVisitor.MaskGuards, Oracle identifier upper-casing, SqlIdentifierHelper, the
/// named-parameter prefix switch in SqlDialectGeneratorBase.FormatParameter and the unbound-only marker styles of Ansi/Snowflake.
/// </summary>
[Trait("Category", "SqlGate")]
public class DialectRegistryCompletenessTests
{
    private static readonly string[] TypeProbes = ["int", "integer", "bigint", "varchar(10)", "varchar2(10)"];

    public static TheoryData<TargetSqlDialect> CapableDialects()
    {
        var data = new TheoryData<TargetSqlDialect>();
        foreach (var dialect in Enum.GetValues<TargetSqlDialect>())
        {
            if (DialectCapabilityTable.Default.TryGet(dialect, out _))
            {
                data.Add(dialect);
            }
        }

        return data;
    }

    public static TheoryData<TargetSqlDialect> IncapableDialects()
    {
        var data = new TheoryData<TargetSqlDialect>();
        foreach (var dialect in Enum.GetValues<TargetSqlDialect>())
        {
            if (!DialectCapabilityTable.Default.TryGet(dialect, out _))
            {
                data.Add(dialect);
            }
        }

        return data;
    }

    [Fact]
    public void SnowflakeAndAnsi_HaveNoCapabilityEntry()
    {
        Assert.False(DialectCapabilityTable.Default.TryGet(TargetSqlDialect.Snowflake, out _));
        Assert.False(DialectCapabilityTable.Default.TryGet(TargetSqlDialect.Ansi, out _));
    }

    [Theory]
    [MemberData(nameof(CapableDialects))]
    public void DialectWithCapability_HasGeneratorBinderAllowlistTypeMapAndMarkerStyle(TargetSqlDialect dialect)
    {
        var caps = DialectCapabilityTable.Default.Get(dialect);
        Assert.Equal(dialect, caps.Dialect);
        Assert.True(Enum.IsDefined(caps.MarkerStyle));

        Assert.NotNull(SqlDialectGeneratorFactory.GetGenerator(dialect));
        Assert.NotNull(CompiledSqlBinders.For(dialect));
        Assert.NotNull(CompiledSqlBinders.CheckedExecutorFor(dialect));
        Assert.NotEmpty(SqlFunctionAllowlists.GetDefault(dialect));
        Assert.Contains(TypeProbes, probe => CatalogTypeMap.TryResolve(dialect, probe, out _, out _));
    }

    [Theory]
    [MemberData(nameof(IncapableDialects))]
    public void DialectWithoutCapability_FailsClosed(TargetSqlDialect dialect)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DialectCapabilityTable.Default.Get(dialect));
        var binder = Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.For(dialect));
        Assert.Equal(SqlCompileNotSupportedReason.Dialect, binder.Reason);
        Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.CheckedExecutorFor(dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "@p0")]
    [InlineData(TargetSqlDialect.PostgreSql, "$1")]
    [InlineData(TargetSqlDialect.DuckDb, "$1")]
    [InlineData(TargetSqlDialect.Sqlite, "?1")]
    [InlineData(TargetSqlDialect.Oracle, ":p1")]
    [InlineData(TargetSqlDialect.Databricks, ":p1")]
    [InlineData(TargetSqlDialect.Snowflake, ":1")]
    [InlineData(TargetSqlDialect.Ansi, "?1")]
    public void UnboundMarker_UsesCapabilityStyle(TargetSqlDialect dialect, string expectedMarker)
    {
        var sql = SqlDialectGeneratorFactory.GetGenerator(dialect).GenerateSql(CompilerTestHelpers.Build("SELECT a FROM t WHERE a = ?"));
        Assert.Contains(expectedMarker, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Databricks_UnboundMarker_UsesCapabilityStyle()
    {
        var sql = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Databricks).GenerateSql(CompilerTestHelpers.Build("SELECT a FROM t WHERE a = ?"));
        Assert.Contains(":p1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("?", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnboundMarker_UnknownDialectWithoutStyle_FailsClosed()
    {
        var context = new TrinoSqlEngine.Ast.Buffer.SqlEmitterContext((TargetSqlDialect)999);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var builder = new TrinoSqlEngine.Ast.Buffer.ValueStringBuilder(stackalloc char[16]);
            try { context.FormatNextParameterMarker(ref builder); }
            finally { builder.Dispose(); }
        });
    }
}
