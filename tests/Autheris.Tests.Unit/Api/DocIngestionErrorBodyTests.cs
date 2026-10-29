namespace Autheris.Tests.Unit.Api;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Docs.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>O-5b step 2 WP-1 backfill (NL-T-20/21): per-site rows for the documentation REST endpoints, invoked through the real route delegates.</summary>
[Trait("Category", "Security")]
public sealed class DocIngestionErrorBodyTests
{
    private const string Canary = "CANARY-5e2b-doc-id";

    private sealed class Permissive(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

        public bool IsService(Type serviceType) => serviceType.IsInterface || inner.GetService(serviceType) != null;

        public bool IsKeyedService(Type serviceType, object? serviceKey) => false;
    }

    private sealed class Routes(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = sp;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static async Task<(int Status, string ContentType, JsonElement Json, string Body)> InvokeAsync(string route, Dictionary<string, string> routeValues, string query = "")
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        var store = Substitute.For<IUniversalDocStore>();
        var search = Substitute.For<IDocSearchService>();
        services.AddSingleton(store);
        services.AddSingleton(search);
        var provider = services.BuildServiceProvider();
        var routes = new Routes(new Permissive(provider));
        routes.MapDocIngestionEndpoints();
        var endpoint = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));

        var ctx = new DefaultHttpContext { RequestServices = provider };
        ctx.Request.QueryString = new QueryString(query);
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-U"), new Claim("tenant_id", "t1")], "Test"));
        foreach (var (k, v) in routeValues)
        {
            ctx.Request.RouteValues[k] = v;
        }

        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        return await ErrorBodyMigrationEndpointTests.ReadAsync(ctx);
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_DocIngestion_ManifestNotFound()
    {
        var r = await InvokeAsync("/api/v1/docs/apps/{appId}/manifest", new() { ["appId"] = Canary });

        ErrorBodyMigrationEndpointTests.AssertGovernedProblem(r, 404, "RESOURCE_NOT_FOUND");
        r.Body.ShouldNotContain(Canary);
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_DocIngestion_ErrorCodeNotFound()
    {
        var r = await InvokeAsync("/api/v1/docs/errors/{errorCode}", new() { ["errorCode"] = Canary });

        ErrorBodyMigrationEndpointTests.AssertGovernedProblem(r, 404, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_DocIngestion_QueryRequired()
    {
        var r = await InvokeAsync("/api/v1/docs/search", new(), "?query=%20&category=" + Canary);

        ErrorBodyMigrationEndpointTests.AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }
}
