namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Phase 5 L-5: the applied virtual filters of a statement are audited once per table, under the resolved qualified name.</summary>
public sealed class GovernedRewriteVirtualFilterKeysTests
{
    private const string Tenant = "tenant_a";

    [Theory]
    [InlineData("SELECT id FROM public.orders")]
    [InlineData("SELECT id FROM orders")]
    public async Task AppliedVirtualFilters_AreKeyedByTheResolvedQualifiedNameOnly(string sql)
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL", SourceName = "default" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().TableName == "orders" ? table : null));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true)
                with { AppliedVirtualFilters = ["vf1"] });
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } });
        var rewriter = new GovernedSqlRewriter(
            options, consentResolution: resolution, tableRepository: repo, consentRepository: consents,
            policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(), policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-L5"), new Claim("tenant_id", Tenant)], "Test"));

        var rewrite = await rewriter.RewriteCoreAsync(sql, user, new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None);

        var filters = rewrite.VirtualFilters.ShouldNotBeNull();
        filters.Keys.ShouldBe([new TableIdentifier("default", "public", "orders").ToQualifiedName()]);
        filters.Values.Single().ShouldBe(["vf1"]);
    }
}
