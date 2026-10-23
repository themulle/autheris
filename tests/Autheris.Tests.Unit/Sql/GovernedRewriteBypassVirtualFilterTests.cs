namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SQLA-19: the single-source path keeps tenant Virtual Filters under the consent bypass without consent services.</summary>
[Trait("Category", "Security")]
public sealed class GovernedRewriteBypassVirtualFilterTests
{
    [Fact]
    public async Task ConsentBypassWithoutConsentServices_VirtualFilterDeny_StaysADeny()
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
        var filters = Substitute.For<IMandatoryRowFilterResolver>();
        filters.ResolveAsync(Arg.Any<MandatoryFilterQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<MandatoryFilterOutcome>(MandatoryFilterOutcome.Deny("uncovered")));
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true }
        });
        var rewriter = new GovernedSqlRewriter(
            options, tableRepository: repo, mandatoryFilters: filters,
            policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(),
            policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-L19"), new Claim("tenant_id", "tenant_a")], "Test"));

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            rewriter.RewriteCoreAsync("SELECT id FROM public.orders", user, new TenantId("tenant_a"), dataSourceName: null, dmlContext: null, CancellationToken.None));
    }
}
