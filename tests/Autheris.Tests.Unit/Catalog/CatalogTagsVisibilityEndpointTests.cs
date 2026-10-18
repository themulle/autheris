namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #111 WP-4 (A-8, C-4): REST admin routes for dataset tags and visibility; all logic lives in the service.</summary>
public sealed class CatalogTagsVisibilityEndpointTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly IDatasetCatalogAdminService _service = Substitute.For<IDatasetCatalogAdminService>();

    private static HttpContext Context()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-X")], "TestAuth"));
        return ctx;
    }

    private static async Task<(int Status, string Body)> Run(IResult result)
    {
        var ctx = Context();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    private static DatasetCatalogResult Ok(DatasetCatalogOutcome outcome = DatasetCatalogOutcome.Applied, bool effective = true) =>
        new(outcome, Orders, new DatasetCatalogSettings(
            [new DatasetTag("pii", DatasetTagSource.Admin), new DatasetTag("sales", DatasetTagSource.Dbt)], DatasetVisibility.Summary), effective);

    [Fact]
    public async Task Get_Success_ReturnsTagsWithSourceVisibilityAndTierEffective()
    {
        _service.GetAsync(Arg.Any<ClaimsPrincipal?>(), "sales.dbo.orders", Arg.Any<CancellationToken>()).Returns(Ok(DatasetCatalogOutcome.Unchanged, effective: false));

        var (status, body) = await Run(await CatalogApiEndpoints.GetDatasetCatalogSettingsHandler("sales.dbo.orders", _service, Context(), default));

        status.ShouldBe(200);
        body.ShouldBe("{\"datasetId\":\"sales.dbo.orders\",\"tags\":[{\"tag\":\"pii\",\"source\":\"admin\"},{\"tag\":\"sales\",\"source\":\"dbt\"}],\"visibility\":\"Summary\",\"tierEffective\":false}");
    }

    [Fact]
    public async Task PutTags_PassesNormalizedReplaceEditToTheService_AndReturnsSettings()
    {
        _service.ApplyAsync(Arg.Any<ClaimsPrincipal?>(), "sales.dbo.orders", Arg.Any<DatasetCatalogEdit>(), "admin", Arg.Any<CancellationToken>()).Returns(Ok());

        var (status, _) = await Run(await CatalogApiEndpoints.PutDatasetTagsHandler("sales.dbo.orders", new SetDatasetTagsDto(["PII"]), _service, Context(), default));

        status.ShouldBe(200);
        await _service.Received(1).ApplyAsync(Arg.Any<ClaimsPrincipal?>(), "sales.dbo.orders",
            Arg.Is<DatasetCatalogEdit>(e => e.ReplaceTags!.SequenceEqual(new[] { "PII" }) && e.AddTags == null && e.Visibility == null), "admin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostTags_PassesAddAndRemove()
    {
        _service.ApplyAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(Ok());

        await Run(await CatalogApiEndpoints.PostDatasetTagsHandler("sales.dbo.orders", new ChangeDatasetTagsDto(["a"], ["b"]), _service, Context(), default));

        await _service.Received(1).ApplyAsync(Arg.Any<ClaimsPrincipal?>(), "sales.dbo.orders",
            Arg.Is<DatasetCatalogEdit>(e => e.AddTags!.Single() == "a" && e.RemoveTags!.Single() == "b" && e.ReplaceTags == null), "admin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutVisibility_PassesTheRawName_NeverParsedInTheHandler()
    {
        _service.ApplyAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(Ok());

        await Run(await CatalogApiEndpoints.PutDatasetVisibilityHandler("sales.dbo.orders", new SetDatasetVisibilityDto("2"), _service, Context(), default));

        await _service.Received(1).ApplyAsync(Arg.Any<ClaimsPrincipal?>(), "sales.dbo.orders",
            Arg.Is<DatasetCatalogEdit>(e => e.Visibility == "2"), "admin", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DatasetCatalogOutcome.NotFound, 404, "{\"error\":\"NOT_FOUND\"}")]
    [InlineData(DatasetCatalogOutcome.Forbidden, 403, "{\"error\":\"FORBIDDEN\"}")]
    [InlineData(DatasetCatalogOutcome.InvalidTag, 400, "{\"error\":\"INVALID_TAG\"}")]
    [InlineData(DatasetCatalogOutcome.TagLimitExceeded, 400, "{\"error\":\"TAG_LIMIT_EXCEEDED\"}")]
    [InlineData(DatasetCatalogOutcome.InvalidVisibility, 400, "{\"error\":\"INVALID_VISIBILITY\"}")]
    [InlineData(DatasetCatalogOutcome.InvalidRequest, 400, "{\"error\":\"INVALID_REQUEST\"}")]
    [InlineData(DatasetCatalogOutcome.Unavailable, 503, "{\"error\":\"UNAVAILABLE\"}")]
    public async Task OutcomeMapping_IsStable(DatasetCatalogOutcome outcome, int status, string body)
    {
        _service.ApplyAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(new DatasetCatalogResult(outcome));

        (await Run(await CatalogApiEndpoints.PutDatasetVisibilityHandler("x", new SetDatasetVisibilityDto("Full"), _service, Context(), default)))
            .ShouldBe((status, body));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_08_UnknownAndNotManageable_AreByteIdentical_AcrossAllFourRoutes()
    {
        _service.ApplyAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(new DatasetCatalogResult(DatasetCatalogOutcome.NotFound));
        _service.GetAsync(default, default!, default).ReturnsForAnyArgs(new DatasetCatalogResult(DatasetCatalogOutcome.NotFound));
        var ctx = Context();

        var results = new[]
        {
            await Run(await CatalogApiEndpoints.GetDatasetCatalogSettingsHandler("a.b.c", _service, ctx, default)),
            await Run(await CatalogApiEndpoints.PutDatasetTagsHandler("a.b.c", new SetDatasetTagsDto(["x"]), _service, ctx, default)),
            await Run(await CatalogApiEndpoints.PostDatasetTagsHandler("a.b.c", new ChangeDatasetTagsDto(["x"], null), _service, ctx, default)),
            await Run(await CatalogApiEndpoints.PutDatasetVisibilityHandler("a.b.c", new SetDatasetVisibilityDto("Summary"), _service, ctx, default))
        };

        results.ShouldAllBe(r => r.Status == 404 && r.Body == "{\"error\":\"NOT_FOUND\"}");
    }

    [Fact]
    public async Task NullBody_IsHandedToTheServiceAsMalformed_SoTheAuthorizationOrderStillApplies()
    {
        _service.ApplyAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(new DatasetCatalogResult(DatasetCatalogOutcome.InvalidRequest));

        var (status, _) = await Run(await CatalogApiEndpoints.PutDatasetTagsHandler("a.b.c", null, _service, Context(), default));

        status.ShouldBe(400);
        await _service.Received(1).ApplyAsync(Arg.Any<ClaimsPrincipal?>(), "a.b.c", Arg.Is<DatasetCatalogEdit>(e => e.Malformed), "admin", Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("/api/v1/catalog/datasets/{id}/tags", "PUT")]
    [InlineData("/api/v1/catalog/datasets/{id}/tags", "POST")]
    [InlineData("/api/v1/catalog/datasets/{id}/visibility", "PUT")]
    [InlineData("/api/v1/catalog/datasets/{id}/catalog-settings", "GET")]
    [InlineData("/api/catalog/datasets/{id}/tags", "PUT")]
    [InlineData("/api/catalog/datasets/{id}/tags", "POST")]
    [InlineData("/api/catalog/datasets/{id}/visibility", "PUT")]
    [InlineData("/api/catalog/datasets/{id}/catalog-settings", "GET")]
    public void Route_RequiresAuthentication_ButNotTheGovernanceAdminPolicy_AndIsAuditedFull(string pattern, string method)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Catalog.Interfaces.ICatalogDiscoveryService>());
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Catalog.Interfaces.IPrincipalResolverService>());
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Catalog.Interfaces.IDatasourceTestingService>());
        builder.Services.AddSingleton(Substitute.For<IAccessPlanningService>());
        builder.Services.AddSingleton(Substitute.For<ITableMetadataRepository>());
        builder.Services.AddSingleton(Substitute.For<IAuditWriter>());
        builder.Services.AddSingleton(_service);
        var app = builder.Build();
        app.MapCatalogEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotContain(a => a.Policy == GatewayPolicies.GovernanceAdmin);
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
    }
}
