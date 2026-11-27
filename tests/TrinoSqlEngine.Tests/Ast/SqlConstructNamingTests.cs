namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

[Trait("Category", "SqlGate")]
[RowPolicyGuardOff]
public sealed class SqlConstructNamingTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private static AstBuilderOptions Exposed() => new AstBuilderOptions { EnforceFunctionPolicy = false }.WithRejectionDetailsExposed();

    private AstBuildException BuildFailure(string sql, AstBuilderOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        return Assert.Throws<AstBuildException>(() => new SqlAstBuilder(options).BuildStatement(tree));
    }

    private CompiledSql Compile(string sql, TargetSqlDialect dialect) => _engine.Compile(sql.AsMemory(), new CompileRequest
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
    }, CancellationToken.None);

    [Fact]
    public void Tablesample_NamedWithBuilderPosition()
    {
        var ex = BuildFailure("SELECT id\nFROM t TABLESAMPLE BERNOULLI (10)", Exposed());
        Assert.Equal(SqlConstructCatalog.TableSample, ex.PublicConstruct);
        Assert.Equal(2, ex.Line);
        Assert.Equal(8, ex.Column);
    }

    [Fact]
    public void Pivot_Named()
    {
        var ex = BuildFailure("SELECT * FROM t PIVOT (SUM(amount) FOR dept IN ('Sales', 'Dev'))", Exposed());
        Assert.Equal(SqlConstructCatalog.Pivot, ex.PublicConstruct);
        Assert.Equal(1, ex.Line);
        Assert.Equal(17, ex.Column);
    }

    [Fact]
    public void GroupByAll_OnSqlServer_NamedAndLocated()
    {
        var ex = Assert.Throws<AstBuildException>(() => Compile("SELECT Region, COUNT(Id) FROM dbo.Orders GROUP BY ALL", TargetSqlDialect.SqlServer));
        Assert.Equal(SqlConstructCatalog.GroupByAll, ex.PublicConstruct);
        Assert.Equal(1, ex.Line);
        Assert.Equal(42, ex.Column);
    }

    [Fact]
    public void Lateral_NamedAndLocated()
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(
            () => Compile("SELECT o.Id FROM dbo.Orders o, LATERAL (SELECT 1 AS x) l", TargetSqlDialect.SqlServer));
        Assert.Equal(SqlConstructCatalog.Lateral, ex.PublicConstruct);
        Assert.Equal(1, ex.Line);
        Assert.Equal(32, ex.Column);
    }

    [Fact]
    public void ConstructInsideStringLiteral_PositionIsTheRealOne()
    {
        var ex = Assert.Throws<AstBuildException>(() => Compile("SELECT 'GROUP BY ALL' AS note, Region FROM dbo.Orders GROUP BY ALL", TargetSqlDialect.SqlServer));
        Assert.Equal(SqlConstructCatalog.GroupByAll, ex.PublicConstruct);
        Assert.Equal(55, ex.Column);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IntervalValueLiteral_NeverEchoed()
    {
        var ex = BuildFailure("SELECT x + INTERVAL 'secret-value' DAY FROM t", new AstBuilderOptions { EnforceFunctionPolicy = false, TranslateTrinoDateFunctions = true }.WithRejectionDetailsExposed());
        Assert.Null(ex.PublicConstruct);
        Assert.Null(ex.Line);
    }

    [Theory]
    [InlineData("SELECT x + INTERVAL 'abc' DAY FROM t")]
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY AUTO")]
    [InlineData("SELECT x FROM t WHERE y = TIMESTAMP 'not a timestamp'")]
    [Trait("Category", "Security")]
    public void DynamicDetailSites_StayUnnamed(string sql)
    {
        var ex = BuildFailure(sql, new AstBuilderOptions { EnforceFunctionPolicy = false, TranslateTrinoDateFunctions = true }.WithRejectionDetailsExposed());
        Assert.Null(ex.PublicConstruct);
        Assert.Null(ex.Line);
        Assert.Null(ex.Column);
    }

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static SelectStatement Select(MaskExpression mask) => new(null,
        new QuerySpecification(false,
            new SelectItem[] { new ColumnSelectItem(mask, new SqlIdentifier("Email", true)) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("t", true) }), null),
            null, null, null), null, null);

    [Fact]
    [Trait("Category", "Security")]
    public void ColumnMaskUnsupported_NeverNamed()
    {
        var rejected = new List<AstBuildException>();
        foreach (var dialect in new[] { TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks, TargetSqlDialect.SqlServer })
        {
            foreach (var kind in new[] { (MaskKind)999 })
            {
                try
                {
                    CompilerTestHelpers.Generate(dialect, Select(new MaskExpression(kind, Col("Email"), new MaskArguments(), "nvarchar(200)")));
                }
                catch (AstBuildException ex)
                {
                    rejected.Add(ex);
                }
                catch (Exception)
                {
                    // a mask that fails for another reason (missing argument) is not the case under test
                }
            }
        }

        Assert.NotEmpty(rejected);
        Assert.All(rejected, ex =>
        {
            Assert.Null(ex.PublicConstruct);
            Assert.Null(ex.Line);
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RowPolicyWithUnsupportedConstruct_NotNamed()
    {
        var context = Context(Orders);
        context.Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lag" };
        var parser = new PolicyExpressionParser(_engine);

        var ex = Assert.Throws<PolicyParseException>(
            () => parser.Parse("lag(Amount) IGNORE NULLS OVER (ORDER BY Id) > 0", context.Build()));
        Assert.DoesNotContain("IGNORE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SqlConstructCatalog.NullTreatment, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RowPolicySubqueryWithEmitTimeConstruct_NotNamed()
    {
        // The client statement says nothing about JOIN ... USING; the row policy subquery does. The emit-time rejection must stay unnamed.
        var (tree, _) = _engine.ParseExpression("EXISTS (SELECT 1 FROM dbo.Orders a JOIN dbo.Orders b USING (Id))".AsMemory(), SqlTokenSecurityOptions.None);
        var expression = new SqlAstBuilder(new AstBuilderOptions { EnforceFunctionPolicy = false }).BuildStandaloneExpression(tree);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(expression, new Dictionary<string, PolicyValue>());

        Exception? thrown = null;
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb })
        {
            try
            {
                Compile("SELECT Id FROM dbo.Orders", dialect);
            }
            catch (Exception ex)
            {
                thrown = ex;
                Assert.False(ex is AstBuildException { PublicConstruct: not null } || ex is SqlCompileNotSupportedException { PublicConstruct: not null },
                    $"{dialect}: {ex.GetType().Name} named a construct that only the policy contains");
            }
        }

        Assert.NotNull(thrown);
    }

    [Fact]
    public void ClientEmitTimeConstruct_StaysNamed_WhenTheKeywordsAreInTheClientText()
    {
        var ex = Assert.Throws<AstBuildException>(() => Compile("SELECT Region, COUNT(Id) FROM dbo.Orders GROUP BY ALL", TargetSqlDialect.SqlServer));
        Assert.Equal(SqlConstructCatalog.GroupByAll, ex.PublicConstruct);
    }

    [Fact]
    public void BuilderDefaultOptions_NeverNameConstruct()
    {
        var ex = BuildFailure("SELECT id FROM t TABLESAMPLE BERNOULLI (10)", new AstBuilderOptions { EnforceFunctionPolicy = false });
        Assert.Null(ex.PublicConstruct);
        Assert.Null(ex.Line);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ExposeRejectionDetails_DefaultsFalse()
    {
        Assert.False(new AstBuilderOptions().ExposeRejectionDetails);
        Assert.True(new AstBuilderOptions().WithRejectionDetailsExposed().ExposeRejectionDetails);
    }

    [Fact]
    public void Catalog_OnlyCataloguedLabelsArePublic()
    {
        Assert.All(SqlConstructCatalog.Anchors.Keys, label => Assert.True(SqlConstructCatalog.IsPublic(label)));
        Assert.False(SqlConstructCatalog.IsPublic("column mask Nullify"));
        Assert.False(SqlConstructCatalog.IsPublic("INTERVAL value 'x'"));
        Assert.False(SqlConstructCatalog.IsPublic(null));
    }
}
