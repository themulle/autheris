using System.Security;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

namespace TrinoSqlEngine.Tests.Ast;

/// <summary>
/// Mutation-gate tests (M-12) of the post-compile table target assertion: every physical table of a compiled statement, wherever it
/// sits in the tree, must map to an authorized target, and anything else is refused with a <see cref="SecurityException"/>.
/// </summary>
[Trait("Category", "SqlGate")]
public class PostCompileTableTargetAssertionVisitorTests
{
    private static readonly (string? Database, string? Schema, string Table)[] Targets =
    {
        ("db1", "sch1", "t1"), (null, "sch2", "t2"), (null, null, "t3"), ("  ", "sch4", "t4")
    };

    private static void AssertTuples(string sql) =>
        PostCompileTableTargetAssertionVisitor.AssertValid(CompilerTestHelpers.Build(sql), Targets);

    private static void AssertNames(string sql, params string[] names) =>
        PostCompileTableTargetAssertionVisitor.AssertValid(CompilerTestHelpers.Build(sql), names);

    [Theory]
    [InlineData("SELECT * FROM db1.sch1.t1")]
    [InlineData("SELECT * FROM DB1.SCH1.T1")]
    [InlineData("SELECT * FROM sch1.t1")]
    [InlineData("SELECT * FROM t1")]
    [InlineData("SELECT * FROM sch2.t2")]
    [InlineData("SELECT * FROM t2")]
    [InlineData("SELECT * FROM t3")]
    [InlineData("SELECT * FROM sch4.t4")]
    [InlineData("SELECT * FROM t4")]
    [InlineData("SELECT * FROM t1 a JOIN sch2.t2 b ON a.id = b.id JOIN t3 c ON c.id = a.id")]
    [InlineData("SELECT * FROM t1 WHERE id IN (SELECT id FROM t2)")]
    [InlineData("WITH x AS (SELECT id FROM t1) SELECT * FROM t3")]
    public void AuthorizedTargets_AreAccepted(string sql) => AssertTuples(sql);

    [Theory]
    [InlineData("SELECT * FROM secret", "secret")]
    [InlineData("SELECT * FROM other.secret", "other.secret")]
    [InlineData("SELECT * FROM x.y.secret", "x.y.secret")]
    [InlineData("SELECT * FROM t1 JOIN secret ON t1.id = secret.id", "secret")]
    [InlineData("SELECT * FROM secret JOIN t1 ON t1.id = secret.id", "secret")]
    [InlineData("SELECT * FROM t1 WHERE id IN (SELECT id FROM secret)", "secret")]
    [InlineData("SELECT * FROM (SELECT id FROM secret) s", "secret")]
    [InlineData("SELECT * FROM t1 UNION ALL SELECT * FROM secret", "secret")]
    [InlineData("WITH x AS (SELECT id FROM secret) SELECT * FROM t1", "secret")]
    [InlineData("SELECT * FROM t1 WHERE EXISTS (SELECT 1 FROM secret)", "secret")]
    public void ForeignTables_AreRefusedAndNamedInTheMessage(string sql, string offender)
    {
        var ex = Assert.Throws<SecurityException>(() => AssertTuples(sql));
        Assert.Contains($"table reference '{offender}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SEC-ADG-02", ex.Message, StringComparison.Ordinal);
        Assert.Throws<SecurityException>(() => AssertNames(sql, "db1.sch1.t1", "t3"));
    }

    [Fact]
    public void NameConstructor_ExpandsQualifiedNamesToTheirSuffixes()
    {
        AssertNames("SELECT * FROM db.sch.t", "db.sch.t");
        AssertNames("SELECT * FROM sch.t", "db.sch.t");
        AssertNames("SELECT * FROM t", "db.sch.t");
        AssertNames("SELECT * FROM sch.t", "sch.t");
        AssertNames("SELECT * FROM t", "sch.t");
        AssertNames("SELECT * FROM T", "t");
        AssertNames("SELECT * FROM a.b", "a.b", "c");
        AssertNames("SELECT * FROM c", "a.b", "c");
        Assert.Throws<SecurityException>(() => AssertNames("SELECT * FROM a.b.zzz", "a.b.c"));
        Assert.Throws<SecurityException>(() => AssertNames("SELECT * FROM a.other", "a.b"));
        Assert.Throws<SecurityException>(() => AssertNames("SELECT * FROM b", "a.b.c"));
        Assert.Throws<SecurityException>(() => AssertNames("SELECT * FROM a", "a.b"));
        Assert.Throws<SecurityException>(() => AssertNames("SELECT * FROM q", Array.Empty<string>()));
    }

    [Fact]
    public void TupleConstructor_WithoutTargets_RefusesEveryTable()
    {
        Assert.Throws<SecurityException>(() =>
            PostCompileTableTargetAssertionVisitor.AssertValid(CompilerTestHelpers.Build("SELECT * FROM t1"), Array.Empty<(string?, string?, string)>()));
    }

    [Fact]
    public void NullArguments_AreRefused()
    {
        var node = CompilerTestHelpers.Build("SELECT 1");
        Assert.Throws<ArgumentNullException>(() => PostCompileTableTargetAssertionVisitor.AssertValid(null!, Targets));
        Assert.Throws<ArgumentNullException>(() => PostCompileTableTargetAssertionVisitor.AssertValid(node, (IEnumerable<(string?, string?, string)>)null!));
        Assert.Throws<ArgumentNullException>(() => PostCompileTableTargetAssertionVisitor.AssertValid(null!, new[] { "t" }));
        Assert.Throws<ArgumentNullException>(() => PostCompileTableTargetAssertionVisitor.AssertValid(node, (IEnumerable<string>)null!));
        Assert.Throws<ArgumentNullException>(() => new PostCompileTableTargetAssertionVisitor((IEnumerable<(string?, string?, string)>)null!));
        Assert.Throws<ArgumentNullException>(() => new PostCompileTableTargetAssertionVisitor((IEnumerable<string>)null!));
    }

    [Fact]
    public void StatementWithoutTables_IsAccepted() =>
        PostCompileTableTargetAssertionVisitor.AssertValid(CompilerTestHelpers.Build("SELECT 1"), Array.Empty<string>());
}
