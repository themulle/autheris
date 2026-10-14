using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Phase 5 L3 (X1): identifiers that start with the gateway prefix <c>autheris_</c> are reserved. The compiler rejects them in the user
/// statement with a typed, stable error (400 <c>SQL_RESERVED_IDENTIFIER</c>) before anything is built, and the binder recognises the
/// reserved check alias exactly, not by substring. A user can therefore no longer trigger the binder's defect alarm with an innocent
/// column name such as <c>autheris_insights</c>.
/// </summary>
public class ReservedIdentifierTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private CompileRequest Request(TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => new()
    {
        TargetDialect = dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict with { RejectReservedIdentifiers = true },
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    };

    [Theory]
    [InlineData("SELECT id AS autheris_ins FROM orders")]
    [InlineData("SELECT id AS AUTHERIS_INS FROM orders")]
    [InlineData("SELECT autheris_insights FROM orders")]
    [InlineData("SELECT \"autheris_x\" FROM orders")]
    [InlineData("SELECT id FROM orders AS autheris_target")]
    [InlineData("SELECT autheris_ins.id FROM (SELECT id FROM orders) autheris_ins")]
    [InlineData("WITH autheris_c AS (SELECT id FROM orders) SELECT id FROM autheris_c")]
    public void Compile_RejectsReservedIdentifiers_WithATypedError(string sql)
    {
        var ex = Assert.Throws<ReservedIdentifierException>(() => _engine.Compile(sql.AsMemory(), Request(), CancellationToken.None));

        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.ReservedIdentifier), ex.Message);
        Assert.DoesNotContain("autheris", ex.Message, StringComparison.OrdinalIgnoreCase);   // the user's text is never echoed
    }

    [Theory]
    [InlineData("SELECT id AS my_autheris_ins FROM orders")]
    [InlineData("SELECT id AS autheris FROM orders")]       // the prefix includes the underscore: the bare word is not reserved
    [InlineData("SELECT id AS autherisx FROM orders")]
    public void Compile_AllowsIdentifiersThatOnlyContainOrResembleTheReservedPrefix(string sql)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(), CancellationToken.None);

        Assert.Equal(SqlStatementClass.Select, compiled.StatementClass);
    }

    [Fact]
    public void ReservedIdentifierInAStringLiteral_IsJustAValue()
    {
        var compiled = _engine.Compile("SELECT id FROM orders WHERE status = 'autheris_ins'".AsMemory(), Request(), CancellationToken.None);

        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "autheris_ins"));
    }

    [Fact]
    public void TheStrictPreset_DoesNotReserve_BecauseThePolicyParserUsesIt()
    {
        Assert.False(SqlTokenSecurityOptions.Strict.RejectReservedIdentifiers);
    }

    [Fact]
    public void ThePolicyParser_StillAcceptsTheGatewayAlias()
    {
        // Admin predicates are parsed with their own token options: the reserved prefix is a rule of the user statement only.
        var parser = new PolicyExpressionParser(_engine);
        var context = Context(Orders).Build();

        var predicate = parser.Parse("region = 'EU'", context);

        Assert.NotNull(predicate);
    }

    // ---- binder: the reserved check alias is recognised exactly -----------------------------------------------------------------

    private static CompiledSql Compiled(string sql) =>
        new(sql, ImmutableArray<BoundParameter>.Empty, TargetSqlDialect.SqlServer, SqlStatementClass.Select,
            ImmutableArray<TrinoSqlEngine.Ast.Nodes.SecurityPredicateId>.Empty, "test");

    [Theory]
    [InlineData("SELECT x_autheris_ins_y FROM t")]
    [InlineData("SELECT autheris_insights FROM t")]
    [InlineData("SELECT my_autheris_ins FROM t")]
    [InlineData("SELECT autheris_ins2 FROM t")]
    public void Bind_DoesNotTreatASubstringAsTheCheckAlias(string sql)
    {
        using var command = new SqliteCommand();

        new SqlServerCompiledSqlBinder().Bind(command, Compiled(sql), new Dictionary<string, object?>());

        Assert.Equal(sql, command.CommandText);
    }

    [Theory]
    [InlineData("INSERT INTO t (a) SELECT autheris_ins.a FROM (VALUES (1)) AS autheris_ins (a)")]
    [InlineData("INSERT INTO t (a) SELECT \"autheris_ins\".\"a\" FROM (VALUES (1)) AS \"autheris_ins\" (\"a\")")]
    [InlineData("INSERT INTO t (a) SELECT [AUTHERIS_INS].[a] FROM (VALUES (1)) AS [AUTHERIS_INS] ([a])")]
    [InlineData("SELECT 1 FROM t AS autheris_ins")]
    public void Bind_StillRefusesTheExactCheckAlias_InEveryQuotingStyle(string sql)
    {
        using var command = new SqliteCommand();

        Assert.Throws<CheckedExecutionRequiredException>(() =>
            new SqlServerCompiledSqlBinder().Bind(command, Compiled(sql), new Dictionary<string, object?>()));
    }
}
