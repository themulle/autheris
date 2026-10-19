namespace Autheris.Tests.Unit.GraphQL;

using System;
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

/// <summary>
/// SR-L-21: the GraphQL catalog is tenant scoped on the admin and discovery paths. TableMetadata has no owner tenant
/// (decision G-11), so tenant ownership is derived from consent records plus Gateway:Catalog:SharedTables.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogTenantIsolationTests
{
    private static readonly TableIdentifier OrdersA = new("sales", "dbo", "orders_a");
    private static readonly TableIdentifier InvoicesB = new("fin", "dbo", "invoices_b");
    private static readonly TableIdentifier Reference = new("ref", "dbo", "countries");

    private static TableMetadata Meta(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table
        {
            SourceName = id.Domain,
            SchemaName = id.Schema,
            TableName = id.TableName,
            DisplayName = id.ToString(),
            Sensitivity = "NORMAL",
            Visibility = DatasetVisibility.Hidden
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static IHttpContextAccessor Accessor(string tenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
            new("tenant_id", tenant)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        return accessor;
    }

    private static (ITableMetadataRepository Metadata, IConsentRepository Consents) Repositories()
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Meta(OrdersA), Meta(InvoicesB), Meta(Reference)]));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetTablesWithConsentsForTenantAsync(new TenantId("tenant-a"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableIdentifier>>([OrdersA]));
        consents.GetTablesWithConsentsForTenantAsync(new TenantId("tenant-b"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableIdentifier>>([InvoicesB]));
        return (metadata, consents);
    }

    private static Task<IReadOnlyList<TableMetadataDto>> Catalog(
        IHttpContextAccessor accessor,
        (ITableMetadataRepository Metadata, IConsentRepository Consents) repos,
        GatewayOptions? options = null)
        => new Query().GetCatalogAsync(
            domain: null, first: null, after: null, search: null, tag: null,
            repos.Metadata, repos.Consents, approvalRepository: null, dataOwnershipRepository: null,
            accessor, Options.Create(options ?? new GatewayOptions()),
            CatalogVisibilityTestFactory.Create(repos.Metadata, repos.Consents, options ?? new GatewayOptions()));

    [Fact]
    public async Task GovernanceAdmin_SeesOnlyTenantTables()
    {
        var catalog = await Catalog(Accessor("tenant-a", "GovernanceAdmin"), Repositories());

        catalog.Select(t => t.TableName).ShouldBe(["orders_a"]);
        catalog.ShouldAllBe(t => t.AccessStatus == "GRANTED");
    }

    [Fact]
    public async Task DiscoveryFlag_NoLongerWidensTheCatalog_ConsentlessCallerSeesNothing()
    {
        var options = new GatewayOptions { Catalog = new DataCatalogOptions { AllowAuthenticatedCatalogDiscovery = true } };

        var catalog = await Catalog(Accessor("tenant-a"), Repositories(), options);

        // WP-0: the flag is deprecated; no consent means no table, in no tenant.
        catalog.ShouldBeEmpty();
    }

    [Fact]
    public async Task SharedTables_AreVisibleToEveryTenant()
    {
        var options = new GatewayOptions { Catalog = new DataCatalogOptions { SharedTables = ["ref.dbo.countries"] } };

        var catalog = await Catalog(Accessor("tenant-a", "GovernanceAdmin"), Repositories(), options);

        catalog.Select(t => t.TableName).ShouldBe(["orders_a", "countries"], ignoreOrder: true);
    }

    [Fact]
    public async Task ClusterAdmin_SeesAll()
    {
        var catalog = await Catalog(Accessor("tenant-a", "ClusterAdmin"), Repositories());

        catalog.Select(t => t.TableName).ShouldBe(["orders_a", "invoices_b", "countries"], ignoreOrder: true);
    }

    [Fact]
    public async Task LegacySingleTenant_GovernanceAdmin_IsNotRestricted()
    {
        // Single-tenant deployment (Gateway:Tenancy:Mode=Single): a GovernanceAdmin without a real tenant claim is unrestricted.
        var options = new GatewayOptions { Tenancy = new TenancyOptions { Mode = "Single" } };
        var catalog = await Catalog(Accessor(TenantId.LegacySingleTenant.Value, "GovernanceAdmin"), Repositories(), options);

        catalog.Count.ShouldBe(3);
    }

    [Fact]
    public async Task InvalidTenantClaim_IsUnauthorized()
    {
        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(
            () => Catalog(Accessor("bad tenant!", "GovernanceAdmin"), Repositories()));

        ex.Errors[0].Code.ShouldBe("UNAUTHORIZED");
        ex.Message.ShouldNotContain("bad tenant");
    }
}
