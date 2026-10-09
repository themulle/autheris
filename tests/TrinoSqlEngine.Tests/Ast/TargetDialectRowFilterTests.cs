namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 3: the gateway renders consent row filters in the target dialect (<c>[dept] = 'Sales'</c> for SQL Server,
/// bound <c>@__gql_*</c> parameters). The AST compiler parsed them again as Trino SQL and rejected brackets and
/// <c>@</c> (WebSQL 400). With <see cref="RlsOptions.PolicyFiltersAreTargetDialectSql"/> they are spliced in verbatim,
/// parenthesized, like the legacy rewriter does; client SQL is still parsed and validated as before.
/// </summary>
public sealed class TargetDialectRowFilterTests
{
    private readonly FastSqlEngine _engine = new();

    private static RlsOptions Options(string filter, TargetSqlDialect dialect, bool trusted = true) => new()
    {
        PolicyProvider = new DefaultRlsPolicyProvider(filter),
        TargetDialect = dialect,
        PolicyFiltersAreTargetDialectSql = trusted
    };

    [Fact]
    public void SqlServer_BracketedFilter_IsSplicedVerbatim()
    {
        string sql = _engine.GenerateGovernedSql(
            "SELECT id FROM orders",
            Options("tenant_id = N't1' AND ([dept] = N'Sales')", TargetSqlDialect.SqlServer));

        Assert.Contains("WHERE (tenant_id = N't1' AND ([dept] = N'Sales'))", sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void ParameterizedFilter_KeepsBoundParameter(TargetSqlDialect dialect)
    {
        string sql = _engine.GenerateGovernedSql("SELECT id FROM orders", Options("dept = @__gql_rf0", dialect));

        Assert.Contains("(dept = @__gql_rf0)", sql);
    }

    [Fact]
    public void Filter_IsParenthesized_SoClientOrCannotEscapeIt()
    {
        string sql = _engine.GenerateGovernedSql(
            "SELECT id FROM orders WHERE id = 1 OR id = 2",
            Options("tenant_id = 't1' OR tenant_id = 't2'", TargetSqlDialect.PostgreSql));

        Assert.Contains("WHERE (tenant_id = 't1' OR tenant_id = 't2')", sql);
    }

    [Fact]
    public void Delete_AppendsTrustedFilter()
    {
        string sql = _engine.GenerateGovernedSql(
            "DELETE FROM orders WHERE id = 5",
            new RlsOptions
            {
                PolicyProvider = new DefaultRlsPolicyProvider("[tenant_id] = N't1'"),
                TargetDialect = TargetSqlDialect.SqlServer,
                PolicyFiltersAreTargetDialectSql = true,
                EnforceReadOnlyQueries = false
            });

        Assert.Contains("([tenant_id] = N't1')", sql);
        Assert.Contains("AND", sql);
    }

    [Fact]
    public void WithoutOption_BracketedFilter_IsStillParsedAndRejected()
    {
        Assert.ThrowsAny<Exception>(() => _engine.GenerateGovernedSql(
            "SELECT id FROM orders",
            Options("[dept] = 'Sales'", TargetSqlDialect.SqlServer, trusted: false)));
    }

    [Fact]
    public void ClientSql_WithBrackets_IsStillRejected_EvenWithOption()
    {
        Assert.ThrowsAny<Exception>(() => _engine.GenerateGovernedSql(
            "SELECT [dept] FROM orders",
            Options("tenant_id = N't1'", TargetSqlDialect.SqlServer)));
    }
}
