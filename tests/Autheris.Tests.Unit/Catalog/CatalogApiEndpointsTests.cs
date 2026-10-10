namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogApiEndpointsTests
{
    private readonly ICatalogDiscoveryService _discoveryService = Substitute.For<ICatalogDiscoveryService>();
    private readonly IPrincipalResolverService _resolverService = Substitute.For<IPrincipalResolverService>();

    private static HttpContext CreateContext(string user = "admin", string tenant = "tenant-a", string[]? roles = null)
    {
        var context = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user),
            new("tenant_id", tenant)
        };
        if (roles != null)
        {
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return context;
    }

    [Fact]
    public void MapCatalogEndpoints_RegistersRoutesWithoutException()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        // Should map without throwing
        app.MapCatalogEndpoints();
    }

    [Fact]
    public async Task SearchCatalog_ReturnsMatchingDatasets()
    {
        var httpContext = CreateContext();
        var expected = new List<CatalogDatasetSummary>
        {
            new("crm.dbo.customers", "crm", "dbo", "customers", "HttpDeclarative", "Confidential", "Customer table", true)
        };

        _discoveryService.SearchCatalogAsync("customers", "crm", Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _discoveryService.SearchCatalogAsync("customers", "crm", new RequestContext(new TenantId("tenant-a"), new Sid("user:admin")));
        result.Count.ShouldBe(1);
        result[0].Table.ShouldBe("customers");
    }

    [Fact]
    public async Task SearchCatalogDetailed_ReturnsMatchingHits()
    {
        var expected = new List<CatalogSearchHit>
        {
            new(
                TableIdentifier: new TableIdentifier("crm", "dbo", "customers"),
                DisplayName: "crm.customers",
                Description: "Customer table",
                Domain: "crm",
                Sensitivity: "Confidential",
                CombinedScore: 0.88,
                Bm25Score: 0.88,
                VectorScore: 0.88,
                MatchedTerms: ["customers"],
                RelevantColumns: ["id", "name"],
                RelatedJoinPaths: [],
                SuggestedGraphQlField: "crm_customers")
        };

        _discoveryService.SearchCatalogDetailedAsync(
            Arg.Is<CatalogSearchQuery>(q => q.QueryText == "customers" && q.DomainFilter == "crm"),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>())
            .Returns(expected);

        var query = new CatalogSearchQuery("customers", "crm", 10, CatalogSearchMode.Hybrid);
        var result = await _discoveryService.SearchCatalogDetailedAsync(query, new RequestContext(new TenantId("tenant-a"), new Sid("user:admin")));
        result.Count.ShouldBe(1);
        result[0].TableIdentifier.TableName.ShouldBe("customers");
        result[0].CombinedScore.ShouldBe(0.88, 0.001);
    }

    [Fact]
    public async Task ResolvePrincipals_ReturnsResolvedList()
    {
        var httpContext = CreateContext();
        var expected = new List<PrincipalResolutionItem>
        {
            new("S-1-5-21-david", "david", "User", true, ["S-1-5-32-operators"])
        };

        _resolverService.ResolvePrincipalAsync("david", Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _resolverService.ResolvePrincipalAsync("david", new RequestContext(new TenantId("tenant-a"), new Sid("user:admin")));
        result.Count.ShouldBe(1);
        result[0].DisplayName.ShouldBe("david");
        result[0].ExactMatch.ShouldBeTrue();
    }
}
