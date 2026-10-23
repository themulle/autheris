namespace Autheris.Tests.Unit.Policy;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-08: the single PDP entry denies when the group resolution of the request was unavailable.</summary>
[Trait("Category", "Security")]
public sealed class TableAccessPolicyDegradedRoleTests
{
    private static readonly TableIdentifier Orders = new("d", "main", "orders");

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
    };

    [Fact]
    public async Task DegradedRoleClaim_DeniesTable()
    {
        var consents = Substitute.For<IConsentRepository>();
        var policy = new TableAccessPolicy(
            consents,
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>(),
            Substitute.For<IPolicyEnforcementService>(),
            Substitute.For<IRebacEvaluator>(),
            null,
            new GatewayOptions(),
            Substitute.For<Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver>());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-ALICE"), new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)], "TestAuth"));
        var query = TableAccessQuery.ForPrincipal(principal, new Sid("S-1-5-21-ALICE"), new TenantId("tenant-a"), Meta());

        var decision = await policy.DecideAsync(query, CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Group resolution unavailable"));
        await consents.ReceivedWithAnyArgs(0).GetActiveConsentsForSubjectsAsync(default!, default, default, default, default);
    }
}
