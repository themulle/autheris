using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Autheris.Tests.Unit;

/// <summary>ARCH-MNT WP4.3 regression: the real async-job endpoint mapping completes (no recursion) and registers its routes.</summary>
public sealed class AsyncJobEndpointMappingTests
{
    [Fact]
    public void MapAsyncJobEndpoints_OnMinimalWebApplication_RegistersJobRoutes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Jobs.Interfaces.IAsyncQueryJobManager>());
        using var app = builder.Build();

        AsyncJobEndpoints.MapAsyncJobEndpoints(app);

        var patterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        patterns.ShouldContain("/api/v1/jobs/query");
    }
}
