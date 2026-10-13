using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate (M-12) tests for <see cref="TenantPredicateFactory"/>: argument validation, the SQLite fail-closed rule, the quoted
/// tenant column and the exact function names of the byte-exact comparison.
/// </summary>
[Trait("Category", "SqlGate")]
public class TenantPredicateFactoryTests
{
    private static DialectCapabilities Caps(TargetSqlDialect dialect) => DialectCapabilityTable.Default.Get(dialect);

    private static BinaryExpression Conjunct(Expression e, int index)
    {
        var list = new List<BinaryExpression>();
        void Walk(Expression x)
        {
            if (x is BinaryExpression { Operator: BinaryOperator.And } and) { Walk(and.Left); Walk(and.Right); }
            else list.Add((BinaryExpression)x);
        }

        Walk(e);
        return list[index];
    }

    [Fact]
    public void Build_NullCapabilities_ThrowsArgumentNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => TenantPredicateFactory.Build(null!, "TenantId", "p", SqlParameterType.String));
        Assert.Equal("capabilities", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Build_EmptyTenantColumn_Throws(string? column)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => TenantPredicateFactory.Build(Caps(TargetSqlDialect.PostgreSql), column!, "p", SqlParameterType.String));
        Assert.Equal("tenantColumn", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Build_EmptyParameterName_Throws(string? name)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => TenantPredicateFactory.Build(Caps(TargetSqlDialect.PostgreSql), "TenantId", name!, SqlParameterType.String));
        Assert.Equal("parameterName", ex.ParamName);
    }

    [Fact]
    public void Build_TenantColumnIsAQuotedIdentifier()
    {
        var predicate = TenantPredicateFactory.Build(Caps(TargetSqlDialect.PostgreSql), "Tenant Id", "p", SqlParameterType.String);
        var column = Assert.IsType<ColumnReference>(Conjunct(predicate, 0).Left);
        var part = Assert.Single(column.Name.Parts);
        Assert.Equal("Tenant Id", part.Value);
        Assert.True(part.IsQuoted);
    }

    [Theory]
    [InlineData("INTEGER")]
    [InlineData("NUMERIC")]
    [InlineData("BLOB")]
    [InlineData("REAL")]
    [InlineData(null)]
    public void Build_Sqlite_NonTextAffinityColumn_FailsClosed(string? columnType)
    {
        var ex = Assert.Throws<SecurityException>(() => TenantPredicateFactory.Build(Caps(TargetSqlDialect.Sqlite), "TenantId", "p", SqlParameterType.String, columnType));
        Assert.Equal("The tenant column of a SQLite table must have TEXT affinity.", ex.Message);
    }

    [Fact]
    public void Build_Sqlite_TextAffinityColumn_BuildsTheBlobComparison()
    {
        var predicate = TenantPredicateFactory.Build(Caps(TargetSqlDialect.Sqlite), "TenantId", "p", SqlParameterType.String, "VARCHAR(20)");
        var binary = Conjunct(predicate, 1);
        Assert.Equal("blob", Assert.IsType<CastExpression>(binary.Left).TargetType);
        Assert.Equal("blob", Assert.IsType<CastExpression>(binary.Right).TargetType);
    }

    [Fact]
    public void Build_NonSqlite_DoesNotRequireATextColumnType()
    {
        TenantPredicateFactory.Build(Caps(TargetSqlDialect.PostgreSql), "TenantId", "p", SqlParameterType.String, "integer");
    }

    [Fact]
    public void ExactEquals_NullArguments_ThrowArgumentNull()
    {
        Expression col = new ColumnReference(new SqlQualifiedName("a"));
        var caps = Caps(TargetSqlDialect.PostgreSql);
        Assert.Equal("capabilities", Assert.Throws<ArgumentNullException>(() => TenantPredicateFactory.ExactEquals(null!, col, col)).ParamName);
        Assert.Equal("column", Assert.Throws<ArgumentNullException>(() => TenantPredicateFactory.ExactEquals(caps, null!, col)).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => TenantPredicateFactory.ExactEquals(caps, col, null!)).ParamName);
    }

    [Fact]
    public void PostgreSql_UsesTextsendOverATextCast()
    {
        var predicate = TenantPredicateFactory.Build(Caps(TargetSqlDialect.PostgreSql), "TenantId", "p", SqlParameterType.String);
        var call = Assert.IsType<FunctionCallExpression>(Conjunct(predicate, 1).Left);
        Assert.Equal("textsend", call.Name.SimpleName);
        Assert.Equal("text", Assert.IsType<CastExpression>(Assert.Single(call.Arguments)).TargetType);
    }

    [Fact]
    public void Oracle_UsesCastToRawOverAVarcharCast()
    {
        var predicate = TenantPredicateFactory.Build(Caps(TargetSqlDialect.Oracle), "TenantId", "p", SqlParameterType.String);
        var call = Assert.IsType<FunctionCallExpression>(Conjunct(predicate, 1).Left);
        Assert.Equal(new[] { "SYS", "UTL_RAW", "CAST_TO_RAW" }, call.Name.Parts.Select(p => p.Value));
    }

    [Fact]
    public void DuckDb_UsesEncodeOverAVarcharCast()
    {
        var predicate = TenantPredicateFactory.Build(Caps(TargetSqlDialect.DuckDb), "TenantId", "p", SqlParameterType.String);
        var call = Assert.IsType<FunctionCallExpression>(Conjunct(predicate, 1).Left);
        Assert.Equal("encode", call.Name.SimpleName);
        Assert.Equal("varchar", Assert.IsType<CastExpression>(Assert.Single(call.Arguments)).TargetType);
    }

    [Fact]
    public void UnknownComparisonStyle_FailsClosedWithTheDialectName()
    {
        var caps = Caps(TargetSqlDialect.PostgreSql) with { TenantComparison = (TenantComparisonStyle)999 };
        var ex = Assert.Throws<NotSupportedException>(() => TenantPredicateFactory.Build(caps, "TenantId", "p", SqlParameterType.String));
        Assert.Equal($"No binary-exact tenant comparison is defined for {caps.Dialect}.", ex.Message);
    }

#if DEBUG
    [Theory]
    [InlineData("UTF8_BINARY", true)]
    [InlineData("UTF8_LCASE", false)]
    [InlineData(null, false)]
    public void Databricks_RelaxationMutant_OnlyAppliesToTheBinaryCollation(string? collation, bool relaxed)
    {
        // The relaxation mutant (SEC-X1-09) must switch the comparison to plain equality only for a UTF8_BINARY column.
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { DisabledControls = new HashSet<SecurityControl> { SecurityControl.DatabricksUtf8BinaryRelaxation } });
        var predicate = TenantPredicateFactory.Build(Caps(TargetSqlDialect.Databricks), "TenantId", "p", SqlParameterType.String, null, collation);
        var equality = Assert.IsType<BinaryExpression>(predicate);
        Assert.Equal(relaxed, equality.Left is ColumnReference);
    }
#endif
}
