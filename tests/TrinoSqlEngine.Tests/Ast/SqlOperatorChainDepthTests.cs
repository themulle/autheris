namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using TrinoSqlEngine.Tests.Security;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

[Trait("Category", "SqlGate")]
[RowPolicyGuardOff]
public sealed class SqlOperatorChainDepthTests
{
    private const int ExpectedChainCap = SqlAstBuilder.MaxOperatorChainTerms;

    private static string BuildOrChainQuery(int terms, string colPrefix = "c")
    {
        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE ");
        for (int i = 0; i < terms; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append(colPrefix).Append(i).Append(" IS NULL");
        }

        return sb.ToString();
    }

    private static string BuildAndChainQuery(int terms, string colPrefix = "c")
    {
        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE ");
        for (int i = 0; i < terms; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append(colPrefix).Append(i).Append(" IS NOT NULL");
        }

        return sb.ToString();
    }

    private static string BuildArithmeticChainQuery(int terms, bool alternatePlusMinus = false)
    {
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < terms; i++)
        {
            if (i > 0) sb.Append(alternatePlusMinus && (i & 1) == 1 ? " - " : " + ");
            sb.Append("Id");
        }

        sb.Append(" FROM dbo.Orders");
        return sb.ToString();
    }

    private static string BuildConcatChainQuery(int terms)
    {
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < terms; i++)
        {
            if (i > 0) sb.Append(" || ");
            sb.Append("Region");
        }

        sb.Append(" FROM dbo.Orders");
        return sb.ToString();
    }

    private static InMemoryTableCatalog WideOrdersCatalog(TargetSqlDialect dialect, int extraColumns)
    {
        string intType = dialect == TargetSqlDialect.Oracle ? "NUMBER(10)" : "integer";
        string strType = dialect == TargetSqlDialect.Oracle ? "VARCHAR2(64)" : "varchar";

        var cols = new List<CatalogColumn>(6 + extraColumns)
        {
            new("Id", intType),
            new("TenantId", strType),
            new("Region", strType),
            new("Status", strType),
            new("Amount", dialect == TargetSqlDialect.Oracle ? "NUMBER(18,2)" : "decimal(18,2)"),
            new("Email", strType)
        };
        for (int i = 0; i < extraColumns; i++)
        {
            cols.Add(new CatalogColumn($"c{i}", strType));
        }

        return new InMemoryTableCatalog(
            new[] { new TableCatalogEntry(Orders, cols.ToImmutableArray(), "TenantId", 1) },
            "dbo");
    }

    [Fact]
    public void Or_Chain_Of_200_Terms_Is_Accepted()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE ");
        for (int i = 0; i < 200; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append("Id = ").Append(i);
        }

        var compiled = env.Compile(sb.ToString());
        Assert.NotEmpty(compiled.Sql);
    }

    [Fact]
    public void And_Chain_Of_200_Terms_Is_Accepted()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE ");
        for (int i = 0; i < 200; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append("Id <> ").Append(i);
        }

        var compiled = env.Compile(sb.ToString());
        Assert.NotEmpty(compiled.Sql);
    }

    [Fact]
    public void Concat_Chain_Of_200_Terms_Is_Accepted()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        var compiled = env.Compile(BuildConcatChainQuery(200));
        Assert.NotEmpty(compiled.Sql);
    }

    [Fact]
    public void Arithmetic_Chain_Of_200_Terms_Is_Accepted()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        var compiled = env.Compile(BuildArithmeticChainQuery(200));
        Assert.NotEmpty(compiled.Sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void Chain_Ast_Is_Identical_To_Recursive_Build(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        env.RegionPolicy();

        string[] queries =
        [
            "SELECT Id FROM dbo.Orders WHERE Id = 1 OR Id = 2 OR Id = 3 OR Id = 4 OR Id = 5",
            "SELECT Id FROM dbo.Orders WHERE Id <> 1 AND Id <> 2 AND Id <> 3 AND Id <> 4 AND Id <> 5",
            "SELECT Id + Id - Id + Id + Id FROM dbo.Orders",
            "SELECT Region || Region || Region || Region || Region FROM dbo.Orders"
        ];

        foreach (string sql in queries)
        {
            var compiled = env.Compile(sql);
            Assert.NotEmpty(compiled.Sql);
            Assert.Contains(compiled.AppliedPredicates, p => p.Ordinal == 0);
            Assert.Contains(compiled.AppliedPredicates, p => p.Ordinal == 1);

            var (tree, _) = env.Engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.Strict);
            var ast = (SelectStatement)new SqlAstBuilder().BuildStatement(tree);
            var spec = (QuerySpecification)ast.Body;
            if (spec.Where is not null)
            {
                var root = Assert.IsType<BinaryExpression>(spec.Where);
                var left = Assert.IsType<BinaryExpression>(root.Left);
                var right = Assert.IsType<BinaryExpression>(root.Right);
                Assert.Equal(root.Operator, left.Operator);
                Assert.NotEqual(root.Operator, right.Operator);
            }
            else
            {
                var expr = ((ColumnSelectItem)spec.Projections[0]).Expression;
                var root = Assert.IsType<BinaryExpression>(expr);
                Assert.IsType<BinaryExpression>(root.Left);
                Assert.IsNotType<BinaryExpression>(root.Right);
                if (sql.Contains(" + ", StringComparison.Ordinal))
                {
                    var col = new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Id", false) }));
                    var expected = new BinaryExpression(
                        new BinaryExpression(
                            new BinaryExpression(
                                new BinaryExpression(col, BinaryOperator.Add, col),
                                BinaryOperator.Subtract,
                                col),
                            BinaryOperator.Add,
                            col),
                        BinaryOperator.Add,
                        col);
                    Assert.True(TrinoSqlEngine.Ast.Security.AstReflection.StructurallyEqual(expected, expr));
                }
            }

            // Golden emitted operator syntax check for the dialect (unchanged vs recursive build)
            if (sql.Contains(" OR ", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(") OR (", compiled.Sql, StringComparison.Ordinal);
            }
            else if (sql.Contains(" AND ", StringComparison.Ordinal))
            {
                Assert.Contains(" AND ", compiled.Sql, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Chain_Security_Visitor_Outcome_Identical_To_Recursive_Build()
    {
        var env = new ProbeEnv(TargetSqlDialect.SqlServer);
        env.RegionPolicy();
        env.MaskEmail();

        var compiled = env.Compile("SELECT Id FROM dbo.Orders WHERE Id = 1 OR Id = 2 OR Id = 3 OR Id = 4 OR Id = 5");
        Assert.Contains("[Region] =", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("[TenantId] =", compiled.Sql, StringComparison.Ordinal);

        Assert.Throws<SecurityException>(() =>
            env.Compile("SELECT Id FROM dbo.Orders WHERE Id = 1 OR Id = 2 OR Id = 3 OR Id = 4 OR Email = 'a@b.c'"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Term_1000_MaskedColumn_Rejected_NoShortCircuit()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        env.RegionPolicy();
        env.MaskEmail();

        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE ");
        for (int i = 0; i < ExpectedChainCap - 1; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append("Id IS NULL");
        }

        sb.Append(" OR Email IS NULL");
        string sql = sb.ToString();

        var ex = Assert.Throws<SecurityException>(() => env.Compile(sql));
        Assert.Contains("Email", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Deep_Parenthesised_Nesting_Is_Still_Rejected()
    {
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < 65; i++) sb.Append('(');
        sb.Append('1');
        for (int i = 0; i < 65; i++) sb.Append(')');
        sb.Append(" FROM dbo.Orders");

        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        Assert.Throws<SecurityException>(() => env.Compile(sb.ToString()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Nested_Subqueries_Still_Count_Per_Level()
    {
        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE Id");
        for (int i = 0; i < 65; i++) sb.Append(" IN (SELECT Id FROM dbo.Orders WHERE Id");
        sb.Append(" = 1");
        for (int i = 0; i < 65; i++) sb.Append(')');

        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        Assert.Throws<SecurityException>(() => env.Compile(sb.ToString()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Mixed_And_Or_Alternation_Counts_Each_Switch()
    {
        var sb = new StringBuilder("SELECT Id FROM dbo.Orders WHERE ");
        for (int i = 0; i < 40; i++) sb.Append('(');
        sb.Append("Id = 0");
        for (int i = 1; i <= 40; i++)
        {
            sb.Append((i & 1) == 1 ? " OR " : " AND ");
            sb.Append("Id = ").Append(i).Append(')');
        }

        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        Assert.Throws<SecurityException>(() => env.Compile(sb.ToString()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Mixed_Operator_Arithmetic_Spine_Counts_As_One_Chain()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        var compiled = env.Compile(BuildArithmeticChainQuery(200, alternatePlusMinus: true));
        Assert.NotEmpty(compiled.Sql);

        string overLimit = BuildArithmeticChainQuery(ExpectedChainCap + 1, alternatePlusMinus: true);
        Assert.Throws<AstBuildException>(() => env.Compile(overLimit));
    }

    [Theory]
    [InlineData("or")]
    [InlineData("and")]
    [InlineData("add")]
    [InlineData("concat")]
    [Trait("Category", "Security")]
    public void Chain_1001_Terms_Rejected_Typed(string kind)
    {
        string sql = kind switch
        {
            "or" => BuildOrChainQuery(ExpectedChainCap + 1),
            "and" => BuildAndChainQuery(ExpectedChainCap + 1),
            "add" => BuildArithmeticChainQuery(ExpectedChainCap + 1),
            "concat" => BuildConcatChainQuery(ExpectedChainCap + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var engine = new FastSqlEngine();
        var (tree, _) = engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.Strict);
        var builder = new SqlAstBuilder();

        Assert.Throws<AstBuildException>(() => builder.BuildStatement(tree));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [Trait("Category", "Security")]
    public void Chain_1000_Terms_FullPipeline_SmallStack_NoStackOverflow(TargetSqlDialect dialect)
    {
        string[] queries =
        [
            BuildOrChainQuery(ExpectedChainCap),
            BuildAndChainQuery(ExpectedChainCap),
            BuildArithmeticChainQuery(ExpectedChainCap, alternatePlusMinus: true),
            BuildConcatChainQuery(ExpectedChainCap)
        ];

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var env = new ProbeEnv(dialect);
                env.RegionPolicy();
                var catalog = WideOrdersCatalog(dialect, ExpectedChainCap);
                var baseReq = env.Request();
                var req = baseReq with
                {
                    Policy = baseReq.Policy with { Catalog = catalog },
                    // This test guards the stack, not the speed: a loaded CI runner must not trip the compile-time budget.
                    CompileTimeout = TimeSpan.FromSeconds(30)
                };

                foreach (string sql in queries)
                {
                    var compiled = env.Engine.Compile(sql.AsMemory(), req, CancellationToken.None);
                    Assert.NotEmpty(compiled.Sql);
                    Assert.Contains(compiled.AppliedPredicates, p => p.Ordinal == 0);
                    Assert.Contains(compiled.AppliedPredicates, p => p.Ordinal == 1);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, maxStackSize: 512 * 1024);

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"Small-stack 1,000-term compile failed on {dialect}: {failure}");
        }
    }
}
