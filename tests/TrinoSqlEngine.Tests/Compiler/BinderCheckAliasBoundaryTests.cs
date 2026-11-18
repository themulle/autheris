using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the binder's defence in depth for check-option INSERTs (CR-ADG-43, CR-ADG-45): the reserved check alias
/// is recognised as a whole identifier wherever it stands in the text, and a longer identifier that merely contains it is not the
/// alias. A missed alias would let a statement that needs the row-count check run unchecked, so every neighbouring character class is
/// pinned for every binder.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class BinderCheckAliasBoundaryTests
{
    public static IEnumerable<object[]> AllBinders() => new[]
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.DuckDb, TargetSqlDialect.PostgreSql, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks
    }.Select(d => new object[] { d });

    private static DbCommandCompiledSqlBinder BinderOf(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => new SqlServerCompiledSqlBinder(),
        TargetSqlDialect.DuckDb => new DuckDbCompiledSqlBinder(),
        TargetSqlDialect.PostgreSql => new PostgreSqlCompiledSqlBinder(),
        TargetSqlDialect.Oracle => new OracleCompiledSqlBinder(),
        _ => new DatabricksCompiledSqlBinder()
    };

    private static CompiledSql Compiled(string sql, TargetSqlDialect dialect) =>
        new(sql, ImmutableArray<BoundParameter>.Empty, dialect, SqlStatementClass.Insert, ImmutableArray<SecurityPredicateId>.Empty, "test");

    private static bool IsRefused(string sql, TargetSqlDialect dialect)
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled(sql, dialect);
        Assert.False(compiled.RequiresRowCountCheck);
        try
        {
            BinderOf(dialect).Bind(cmd, compiled, new Dictionary<string, object?>());
        }
        catch (CheckedExecutionRequiredException ex)
        {
            Assert.Equal(dialect, ex.Dialect);
            Assert.Equal(string.Empty, cmd.CommandText);
            return true;
        }
        catch (System.Security.SecurityException) when (dialect == TargetSqlDialect.Oracle)
        {
            // the Oracle binder needs BindByName, which the provider-neutral test command does not have: the refusal check ran first and passed
            return false;
        }

        Assert.Equal(sql, cmd.CommandText);
        return false;
    }

    [Theory]
    [MemberData(nameof(AllBinders))]
    public void WholeAlias_IsRecognisedInEveryPosition(TargetSqlDialect dialect)
    {
        foreach (string sql in new[]
                 {
                     "autheris_ins",
                     "AUTHERIS_INS",
                     "Autheris_Ins",
                     "autheris_ins ",
                     " autheris_ins",
                     "SELECT autheris_ins.a FROM x",
                     "SELECT a FROM x AS autheris_ins",
                     "SELECT a FROM x AS autheris_ins(a)",
                     "SELECT \"autheris_ins\".a FROM x",
                     "SELECT [autheris_ins].a FROM x",
                     "SELECT `autheris_ins`.a FROM x",
                     "SELECT (autheris_ins) FROM x",
                     "SELECT a,autheris_ins,b FROM x",
                     "SELECT a FROM x WHERE autheris_ins=1",
                     "SELECT a FROM x WHERE 1=autheris_ins",
                     "SELECT a FROM x\tautheris_ins\n",
                     "SELECT a-autheris_ins FROM x",
                     "SELECT autheris_insights, autheris_ins FROM x",
                     "SELECT xautheris_ins1, autheris_ins FROM x",
                     "SELECT autheris_ins1 FROM autheris_ins"
                 })
        {
            Assert.True(IsRefused(sql, dialect), sql);
        }
    }

    [Theory]
    [MemberData(nameof(AllBinders))]
    public void LongerIdentifiers_AreNotTheAlias(TargetSqlDialect dialect)
    {
        foreach (string sql in new[]
                 {
                     "SELECT autheris_insights FROM x",
                     "SELECT autheris_ins1 FROM x",
                     "SELECT autheris_ins_ FROM x",
                     "SELECT autheris_ins$ FROM x",
                     "SELECT autheris_ins# FROM x",
                     "SELECT xautheris_ins FROM x",
                     "SELECT 1autheris_ins FROM x",
                     "SELECT _autheris_ins FROM x",
                     "SELECT $autheris_ins FROM x",
                     "SELECT #autheris_ins FROM x",
                     "SELECT Autheris_Insert FROM x",
                     "autheris_insx",
                     "xautheris_ins",
                     "autheris_in",
                     "autheris ins",
                     "SELECT a FROM x"
                 })
        {
            Assert.False(IsRefused(sql, dialect), sql);
        }
    }

    [Fact]
    public void Binder_RefusesNullStatements_AndBindsThePlainText()
    {
        var binder = new SqlServerCompiledSqlBinder();
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        Assert.Throws<ArgumentNullException>(() => binder.Bind(cmd, null!, new Dictionary<string, object?>()));

        binder.Bind(cmd, Compiled("DELETE FROM t", TargetSqlDialect.SqlServer), new Dictionary<string, object?>());
        Assert.Equal("DELETE FROM t", cmd.CommandText);
    }
}
