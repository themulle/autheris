using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-01: a CTE name must never shadow a physical table differently in the gateway and in the database.</summary>
[Trait("Category", "SqlGate")]
public class CteShadowingTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public static IEnumerable<object[]> Dialects() => new[]
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks
    }.Select(d => new object[] { d });

    private CompiledSql Compile(string sql, TargetSqlDialect dialect, bool allowRecursive = true, StatementPermissions statements = StatementPermissions.ReadOnly) => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = dialect,
        AllowRecursiveCte = allowRecursive,
        Statements = statements,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        AllowExperimentalDialect = true,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    }, CancellationToken.None);

    private static (string Definition, string Reference) DefinitionAndReference(string sql)
    {
        var def = Regex.Match(sql, @"WITH\s+(?<d>""[^""]+""|\[[^\]]+\]|`[^`]+`)\s+AS", RegexOptions.IgnoreCase);
        var reference = Regex.Match(sql, @"FROM\s+(?<r>""[^""]+""|\[[^\]]+\]|`[^`]+`)(?!\.)", RegexOptions.IgnoreCase);
        Assert.True(def.Success, sql);
        Assert.True(reference.Success, sql);
        return (def.Groups["d"].Value, reference.Groups["r"].Value);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void QuotedCteVsUnquotedPhysical_CaseVariants_AlwaysSecured(TargetSqlDialect dialect)
    {
        foreach (string reference in new[] { "orders", "Orders", "ORDERS" })
        {
            var c = Compile($"WITH \"orders\" AS (SELECT 1 AS id) SELECT id FROM {reference}", dialect);
            var (definition, emittedReference) = DefinitionAndReference(c.Sql);

            // The reference is printed from the definition identifier: same delimited, case-exact text.
            Assert.Equal(definition, emittedReference);
            Assert.Contains("orders", definition, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void UnquotedCte_DefinitionAndReference_AreIdenticalDelimitedIdentifiers(TargetSqlDialect dialect)
    {
        var c = Compile("WITH Orders AS (SELECT 1 AS id) SELECT id FROM ORDERS", dialect);
        var (definition, emittedReference) = DefinitionAndReference(c.Sql);
        Assert.Equal(definition, emittedReference);
        Assert.Contains("orders", definition, StringComparison.Ordinal);   // the gateway's scope key (lower case)
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void CteShadowingPhysicalName_StillSecuresTheQualifiedPhysicalTableInsideTheBody(TargetSqlDialect dialect)
    {
        var c = Compile("WITH orders AS (SELECT id FROM dbo.orders) SELECT id FROM orders", dialect);
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), c.AppliedPredicates);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void QualifiedColumnReferenceToACte_KeepsBinding(TargetSqlDialect dialect)
    {
        var c = Compile("WITH orders AS (SELECT 1 AS id) SELECT orders.id FROM orders", dialect);
        Assert.Matches(@"(?i)FROM\s+\S+\s+(AS\s+)?\S+", c.Sql);   // an explicit alias carries the user spelling
    }

    private const string RecursiveChain =
        "WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT c.id, c.n + 1 FROM chain c WHERE c.n < 3) SELECT id, n FROM chain";

    /// <summary>CR-ADG-29: a recursive CTE resolves its own name, and the physical tables of both members stay secured.</summary>
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_SelfReferenceResolvesToTheCte_AndBaseTablesAreSecured(TargetSqlDialect dialect)
    {
        var c = Compile(RecursiveChain, dialect);
        Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), c.AppliedPredicates);
        // Both the definition and the self-reference use the same delimited identifier.
        Assert.True(Regex.Count(c.Sql, @"[""`\[]chain[""`\]]") >= 3, c.Sql);   // definition, self-reference, outer reference
        Assert.Equal(1, Regex.Count(c.Sql, @"(?i)dbo\W+orders\W"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_JoiningAnotherCatalogTable_SecuresIt(TargetSqlDialect dialect)
    {
        var c = Compile(
            "WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT c.id, c.n + 1 FROM chain c JOIN orders o ON o.id = c.id WHERE c.n < 3) SELECT id FROM chain",
            dialect);
        Assert.Equal(2, Regex.Count(c.Sql, @"(?i)dbo\W+orders\W"));   // both members read the secured physical table
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_NamedLikeACatalogTable_IsRejected(TargetSqlDialect dialect)
    {
        Assert.Throws<System.Security.SecurityException>(() => Compile(
            "WITH RECURSIVE orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT o.id, o.n + 1 FROM orders o WHERE o.n < 3) SELECT id FROM orders",
            dialect));
        Assert.Throws<System.Security.SecurityException>(() => Compile(
            "WITH RECURSIVE \"Orders\" (id, n) AS (SELECT 1, 1 UNION ALL SELECT o.id, o.n + 1 FROM \"Orders\" o WHERE o.n < 3) SELECT id FROM \"Orders\"",
            dialect));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void NestedCteNamesDifferingOnlyInCase_AreRejected(TargetSqlDialect dialect)
    {
        Assert.Throws<System.Security.SecurityException>(() => Compile(
            "WITH \"Chain\" AS (SELECT 1 AS id) SELECT id FROM (WITH \"chain\" AS (SELECT 2 AS id) SELECT id FROM \"chain\") t", dialect));
        Assert.Throws<System.Security.SecurityException>(() => Compile(
            "WITH \"Chain\" AS (SELECT 1 AS id), \"chain\" AS (SELECT 2 AS id) SELECT id FROM \"chain\"", dialect));
    }

    /// <summary>
    /// CR-ADG-32 / SEC-277-04: a self-reference needs WITH RECURSIVE on every dialect, even where the database would allow it without. The
    /// answer is the typed "construct not supported" (422), the same type the router's pre-check throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(Dialects))]
    public void NonRecursiveWith_SelfReference_ThrowsConstructNotSupported(TargetSqlDialect dialect)
    {
        foreach (string sql in new[]
        {
            "WITH chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT c.id, c.n + 1 FROM chain c WHERE c.n < 3) SELECT id, n FROM chain",
            "WITH orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT o.id, o.n + 1 FROM orders o WHERE o.n < 3) SELECT id FROM orders",
            "WITH \"chain\" AS (SELECT id FROM (SELECT id FROM \"chain\") x) SELECT id FROM \"chain\""
        })
        {
            var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(sql, dialect));
            Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
        }
    }

    /// <summary>SEC-277-03: the default request does not allow WITH RECURSIVE; the typed rejection comes before every other recursive check.</summary>
    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_WhenNotAllowed_ThrowsConstructNotSupported(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(RecursiveChain, dialect, allowRecursive: false));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    /// <summary>SEC-277-03: a recursive WITH in INSERT, UPDATE, DELETE and MERGE is rejected even when the request allows recursion.</summary>
    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_InDml_ThrowsConstructNotSupported(TargetSqlDialect dialect)
    {
        const string Recursive = "WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r";
        foreach (string sql in new[]
        {
            "INSERT INTO orders (id, status) " + Recursive.Replace("SELECT n FROM r", "SELECT n, 'x' FROM r"),
            "UPDATE orders SET status = 'x' WHERE id IN (" + Recursive + ")",
            "DELETE FROM orders WHERE id IN (" + Recursive + ")",
            "MERGE INTO orders t USING (" + Recursive + ") s ON t.id = s.n WHEN MATCHED THEN UPDATE SET status = 'x'"
        })
        {
            foreach (bool allow in new[] { true, false })
            {
                // INSERT ... WITH is already refused by the AST builder; every other class reaches the compiler gate.
                var error = Record.Exception(() => Compile(sql, dialect, allow, AllDml));
                Assert.True(
                    error is SqlCompileNotSupportedException || (sql.StartsWith("INSERT", StringComparison.Ordinal) && error is TrinoSqlEngine.Ast.Builder.AstBuildException),
                    $"{dialect}/{allow}: {sql} -> {error?.GetType().Name}");
            }
        }
    }

    private const StatementPermissions AllDml =
        StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge;

    [Theory]
    [MemberData(nameof(Dialects))]
    public void NonRecursiveWith_WithoutSelfReference_StillCompiles(TargetSqlDialect dialect)
    {
        Compile("WITH a AS (SELECT id FROM orders), b AS (SELECT id FROM a) SELECT id FROM b", dialect);
        Compile("WITH a AS (WITH a AS (SELECT 1 AS id) SELECT id FROM a) SELECT id FROM a", dialect);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveKeyword_IsEmittedPerDialect(TargetSqlDialect dialect)
    {
        var c = Compile(RecursiveChain, dialect);
        bool keyword = Regex.IsMatch(c.Sql, @"(?i)\bWITH\s+RECURSIVE\b");
        Assert.Equal(dialect is not (TargetSqlDialect.SqlServer or TargetSqlDialect.Oracle), keyword);
    }
}
