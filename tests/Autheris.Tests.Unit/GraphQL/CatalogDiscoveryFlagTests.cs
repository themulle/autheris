namespace Autheris.Tests.Unit.GraphQL;

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>T-W0-11 (PLAN-GOV-111 A-0.4): with <c>AllowAuthenticatedCatalogDiscovery</c> on, non-administrators still take the consent path.</summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryFlagTests
{
    private static readonly TableMetadata Orders = Wp0.Table("sales", "orders", columns: ["id", "amount", "secret_note"]);
    private static readonly TableMetadata Invoices = Wp0.Table("fin", "invoices", columns: ["id"]);

    private static Task<IReadOnlyList<TableMetadataDto>> CatalogAsync(ClaimsPrincipal user, bool flag, TenancyMode mode = TenancyMode.Multi, params Consent[] consents)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });
        var gateway = new GatewayOptions
        {
            Tenancy = Wp0.Options(mode).Tenancy,
            Catalog = new DataCatalogOptions { AllowAuthenticatedCatalogDiscovery = flag }
        };
        var tables = Wp0.Tables(Orders, Invoices);
        var consentRepo = Wp0.Consents(consents);
        return new Query().GetCatalogAsync(
            domain: null, first: null, after: null, search: null, tag: null,
            tables, consentRepo, approvalRepository: null, dataOwnershipRepository: null,
            accessor, Options.Create(gateway), CatalogVisibilityTestFactory.Create(tables, consentRepo, gateway));
    }

    [Fact]
    public async Task FlagOn_ConsentlessCaller_GetsNothing()
        => (await CatalogAsync(Wp0.User("S-NONE", "tenant-a"), flag: true)).ShouldBeEmpty();

    [Fact]
    public async Task FlagOn_ConsentedCaller_GetsOnlyConsentedTablesAndColumns()
    {
        var catalog = await CatalogAsync(Wp0.User("S-A", "tenant-a"), flag: true, consents: Wp0.Allow(Orders.Identifier, "S-A", Wp0.TenantA, "id", "amount"));

        var table = catalog.ShouldHaveSingleItem();
        table.TableName.ShouldBe("orders");
        table.Columns.ShouldBe(["id", "amount"]);
    }

    [Fact]
    public async Task FlagOn_ClaimlessGovernanceAdmin_Multi_IsNotAnAdministrator()
        => (await CatalogAsync(Wp0.User("S-ADM", null, ["GovernanceAdmin"]), flag: true)).ShouldBeEmpty();

    [Fact]
    public async Task FlagOn_SingleModeGovernanceAdminAndClusterAdmin_Unchanged()
    {
        (await CatalogAsync(Wp0.User("S-ADM", null, ["GovernanceAdmin"]), flag: true, TenancyMode.Single)).Count.ShouldBe(2);
        (await CatalogAsync(Wp0.User("root", "tenant-a", ["ClusterAdmin"]), flag: true)).Count.ShouldBe(2);
    }
}
