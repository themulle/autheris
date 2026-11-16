namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// A gateway-generated LIKE parameter (tree filter value of a planned read) compiles on SQL Server, wrapped so that a bracket in the value
/// is literal; every other non-literal SQL Server LIKE pattern, and any user SQL naming a gateway parameter, stays rejected.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernedSqlServerGatewayLikeTests
{
    private const string Tenant = "tenant_a";
    private const string Sentinel = "SENTINEL[x]-VALUE";

    private const string AggregateSql =
        "SELECT region, COUNT(*) AS n FROM dbo.orders WHERE region LIKE @__gql_tf_0 ESCAPE '!' GROUP BY region";

    private static GovernedSqlRewriter CreateRewriter(string sourceType = "SqlServer")
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "dbo", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "dbo", SourceType = sourceType, SourceName = "default" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "region", DataType = "nvarchar(100)" }
            ]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().TableName == "orders" ? table : null));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } });
        return new GovernedSqlRewriter(
            options, consentResolution: resolution, tableRepository: repo, consentRepository: consents,
            policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies());
    }

    private static ClaimsPrincipal User() =>
        Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-L5"), new Claim("tenant_id", Tenant)], "Test")));

    private static GovernedSqlQueryRequest Planned(string sql, string value) =>
        new(sql, null, "default", Transport: SqlTransports.Rest, PlannedTableRead: true)
        {
            GatewayParameters = new Dictionary<string, object?> { ["@__gql_tf_0"] = value }
        };

    private static (string Text, IReadOnlySet<string> Aliases) Prepare(GovernedSqlQueryRequest request)
    {
        var merged = GovernedSqlExecutionService.MergeGatewayParameters(request);
        var text = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, merged, request.GatewayParameters, out _);
        return (text, GovernedSqlExecutionService.GatewayParameterAliases(request.GatewayParameters));
    }

    [Fact]
    public async Task PlannedAggregate_ContainsFilter_CompilesOnSqlServer_WithBracketSafeWrapper_AndBoundValue()
    {
        var predicate = TreeFilterClauseBuilder.Build(
            new TreeComparison("region", TreeFilterOperator.Contains, Sentinel),
            _ => new ColumnRef("region", ColumnValueKind.String)).SqlPredicate;
        predicate.ShouldContain("LIKE @__gql_tf_0");

        var (text, aliases) = Prepare(Planned(AggregateSql, "%" + Sentinel + "%"));
        var rewrite = await CreateRewriter().RewriteCoreAsync(
            text, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None,
            plannedTableRead: true, gatewayLikePatternParameters: aliases);

        rewrite.Compiled.Sql.ShouldContain("REPLACE(CAST(");
        rewrite.Compiled.Sql.ShouldContain(" ESCAPE ");
        rewrite.Compiled.Sql.ShouldNotContain("SENTINEL");
        rewrite.Compiled.Parameters.ShouldContain(p => p.SourceName == "gw_tf_0");
        // The value is bound by name and resolved at execution: it never appears in the SQL text.
        rewrite.Compiled.Sql.ShouldNotContain(Sentinel);
    }

    [Fact]
    public async Task PlannedAggregate_CompiledWithAllowList_IsNotReusedWithoutIt()
    {
        var rewriter = CreateRewriter();
        var (text, aliases) = Prepare(Planned(AggregateSql, "%a%"));
        await rewriter.RewriteCoreAsync(
            text, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None,
            plannedTableRead: true, gatewayLikePatternParameters: aliases);

        await Should.ThrowAsync<SqlCompileNotSupportedException>(() => rewriter.RewriteCoreAsync(
            text, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None, plannedTableRead: true));
    }

    [Fact]
    public async Task PlannedAggregate_OnPostgreSql_HasNoSqlServerWrapper()
    {
        var (text, aliases) = Prepare(Planned(AggregateSql, "%a%"));
        var rewrite = await CreateRewriter("PostgreSql").RewriteCoreAsync(
            text, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None,
            plannedTableRead: true, gatewayLikePatternParameters: aliases);

        rewrite.Compiled.Sql.ShouldNotContain("REPLACE(");
    }

    [Fact]
    public void TreeFilterClauseBuilder_NeverEscapesBracket_SoTheGatewayWrapperDoesNotDoubleEscape()
    {
        // The SQL Server wrapper doubles '[' with the clause's escape character. That is only right while the builder leaves '[' alone:
        // a pattern source that pre-escapes '[' must never be placed on the gateway allow-list.
        var clause = TreeFilterClauseBuilder.Build(
            new TreeComparison("region", TreeFilterOperator.Contains, "a[b_c"),
            _ => new ColumnRef("region", ColumnValueKind.String));

        clause.Parameters.Values.ShouldHaveSingleItem().ShouldBe("%a[b!_c%");
    }

    [Fact]
    public async Task PlannedAggregate_WithoutTheAllowList_IsRejected()
    {
        var (text, _) = Prepare(Planned(AggregateSql, "%a%"));

        await Should.ThrowAsync<SqlCompileNotSupportedException>(() => CreateRewriter().RewriteCoreAsync(
            text, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None, plannedTableRead: true));
    }

    [Theory]
    [InlineData("SELECT id FROM dbo.orders WHERE region LIKE __param_userp ESCAPE '!'")]
    [InlineData("SELECT id FROM dbo.orders WHERE region LIKE region")]
    [InlineData("SELECT id FROM dbo.orders WHERE region LIKE concat('a', '%')")]
    public async Task UserSql_NonLiteralLikePattern_IsRejectedOnSqlServer(string sql) =>
        await Should.ThrowAsync<SqlCompileNotSupportedException>(() => CreateRewriter().RewriteCoreAsync(
            sql, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None,
            gatewayLikePatternParameters: GovernedSqlExecutionService.GatewayParameterAliases(null)));

    [Theory]
    [InlineData("gw_tf_0")]
    [InlineData("@GW_TF_7")]
    public void ClientParameterNamedLikeAGatewayParameter_IsRejected(string name)
    {
        var parameters = new Dictionary<string, object?> { [name] = "x" };

        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.EnsureClientParameterNames(parameters));
        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.EnsureClientParameterNames(parameters, plannedTableRead: true));
    }

    [Fact]
    public void GatewayParameterAliases_AreEmptyWithoutAGatewayMap_AndExactOtherwise()
    {
        GovernedSqlExecutionService.GatewayParameterAliases(null).ShouldBeEmpty();
        GovernedSqlExecutionService.GatewayParameterAliases(new Dictionary<string, object?> { ["@__gql_tf_3"] = 1 }).ShouldBe(["gw_tf_3"]);
        Should.Throw<InvalidOperationException>(() => GovernedSqlExecutionService.GatewayParameterAliases(new Dictionary<string, object?> { ["@evil"] = 1 }));
    }
}
