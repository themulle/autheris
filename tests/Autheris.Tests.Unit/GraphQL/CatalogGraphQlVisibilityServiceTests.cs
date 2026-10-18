namespace Autheris.Tests.Unit.GraphQL;

using System.Security.Claims;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #138 INC-1 P3: GraphQL <c>catalog</c> runs through <see cref="ICatalogVisibilityService"/> (SEC-CAT138-13, -16, -19; A-1 decisions 2, 6, 8).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogGraphQlVisibilityServiceTests
{
    private static readonly TableMetadata Orders = Full("orders");
    private static readonly TableMetadata Invoices = Full("invoices");

    private static TableMetadata Full(string name) => Wp0.Table("sales", name, "d " + name, ["id", "secret"]) with { CatalogView = CatalogView.Granted };

    private static GatewayOptions Opts(TenancyMode mode) => new()
    {
        Tenancy = new TenancyOptions { Mode = mode.ToString() },
        Rebac = new RebacOptions { Enabled = false }
    };

    private static Task<IReadOnlyList<TableMetadataDto>> Catalog(
        ClaimsPrincipal user,
        ITableMetadataRepository tables,
        IConsentRepository serviceConsents,
        GatewayOptions options,
        IConsentRepository? graphQlConsents = null,
        ICatalogVisibilityService? service = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });
        return new Query().GetCatalogAsync(
            domain: null, first: null, after: null, search: null, tag: null,
            tables, graphQlConsents ?? serviceConsents, approvalRepository: null, dataOwnershipRepository: null,
            accessor, Options.Create(options),
            service ?? CatalogVisibilityTestFactory.Create(tables, serviceConsents, options));
    }

    [Fact]
    public async Task GraphQl_Unrestricted_SameSetAsRestAndMcp()
    {
        // Single mode, GovernanceAdmin without tenant claim: no consent exists for either table; the engine rule is the whole catalog.
        var admin = Wp0.User("S-GOV", null, ["GovernanceAdmin"]);
        var tables = Wp0.Tables(Orders, Invoices);
        var consents = Wp0.Consents();
        var options = Opts(TenancyMode.Single);
        var service = CatalogVisibilityTestFactory.Create(tables, consents, options);

        var graphQl = await Catalog(admin, tables, consents, options, service: service);

        var rest = (await service.GetVisibleAsync(CatalogVisibilityTestFactory.Caller(admin, CatalogChannel.Rest))).Tables;
        var mcp = (await service.GetVisibleAsync(CatalogVisibilityTestFactory.Caller(admin, CatalogChannel.Mcp))).Tables;
        rest.Count.ShouldBe(2);
        mcp.Count.ShouldBe(2);
        graphQl.Select(t => t.TableName).OrderBy(n => n).ShouldBe(rest.Select(t => t.Table.TableName).OrderBy(n => n));
    }

    [Fact]
    public async Task GraphQl_AccessStatus_DoesNotChangeRowSet()
    {
        var user = Wp0.User("S-USER", "tenant-a");
        var tables = Wp0.Tables(Orders, Invoices);
        var serviceConsents = Wp0.Consents(Wp0.Allow(Orders.Identifier, "S-USER", Wp0.TenantA, "id"));
        var options = Opts(TenancyMode.Multi);

        // The status load sees different consents than the service: it must not add or remove rows.
        var broader = Wp0.Consents(
            Wp0.Allow(Orders.Identifier, "S-USER", Wp0.TenantA, "id"), Wp0.Allow(Invoices.Identifier, "S-USER", Wp0.TenantA, "id"));
        var empty = Wp0.Consents();

        (await Catalog(user, tables, serviceConsents, options, graphQlConsents: broader)).Select(t => t.TableName).ShouldBe(["orders"]);
        (await Catalog(user, tables, serviceConsents, options, graphQlConsents: empty)).Select(t => t.TableName).ShouldBe(["orders"]);
    }

    [Fact]
    public async Task Shape_ComesFromTheServiceSet_NotRecomputedByGraphQl()
    {
        // A service set that says "non-admin view" for a Global administrator: the channel must follow the set, not the principal.
        var admin = Wp0.User("root", "tenant-a", ["ClusterAdmin"]);
        var unmarked = Wp0.Table("sales", "u_t", "U", ["id"]) with { CatalogView = CatalogView.Summary };
        var service = Substitute.For<ICatalogVisibilityService>();
        service.GetVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<Autheris.Application.Governance.Contracts.SchemaContractDefinition?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVisibleSet>(new CatalogVisibleSet([unmarked], CallerScope.Subject, isAdminView: false)));

        var result = await Catalog(admin, Wp0.Tables(unmarked), Wp0.Consents(), Opts(TenancyMode.Multi), service: service);

        result.Single().Columns.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingService_Empty_FailClosed()
    {
        var user = Wp0.User("S-USER", "tenant-a");
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });

        var result = await new Query().GetCatalogAsync(
            null, null, null, null, null, Wp0.Tables(Orders), Wp0.Consents(Wp0.Allow(Orders.Identifier, "S-USER", Wp0.TenantA, "id")),
            null, null, accessor, Options.Create(Opts(TenancyMode.Multi)));

        result.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ClusterAdmin")]
    [InlineData("GovernanceAdmin")]
    [InlineData("Subject")]
    public async Task NameOnlyPrincipal_NoSid_Empty_AllChannels_GraphQl(string role)
    {
        var tables = Wp0.Tables(Orders);
        var consents = Wp0.Consents();
        var nameOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.Role, role), new Claim("tenant_id", "tenant-a")], "Bearer"));

        var ex = await Should.ThrowAsync<GraphQLException>(() => Catalog(nameOnly, tables, consents, Opts(TenancyMode.Multi)));

        ex.Errors[0].Code.ShouldBe("UNAUTHORIZED");
        await tables.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
        await consents.DidNotReceiveWithAnyArgs().GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ChannelCheck_StaysSecondLineOfDefence_NonAdminUnmarkedRowFromAnyServiceIsDropped()
    {
        var user = Wp0.User("S-USER", "tenant-a");
        var unmarked = Wp0.Table("sales", "u_t", "U", ["id"]) with { CatalogView = CatalogView.Unmarked };
        var service = Substitute.For<ICatalogVisibilityService>();
        service.GetVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<Autheris.Application.Governance.Contracts.SchemaContractDefinition?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVisibleSet>(new CatalogVisibleSet([unmarked], CallerScope.Subject, isAdminView: false)));

        (await Catalog(user, Wp0.Tables(unmarked), Wp0.Consents(), Opts(TenancyMode.Multi), service: service)).ShouldBeEmpty();
    }
}
