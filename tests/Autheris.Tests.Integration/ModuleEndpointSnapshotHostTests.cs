namespace Autheris.Tests.Integration;

using Autheris.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>Endpoint ownership verification against the data sources of a real application (no container needed).</summary>
public sealed class ModuleEndpointSnapshotHostTests
{
    [Fact]
    public void Host_endpoints_are_not_treated_as_module_endpoints_in_a_real_application()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var holder = builder.Build();
        IEndpointRouteBuilder app = holder;

        // Host endpoint without module metadata, mapped before the module.
        app.MapGet("/api/v1/host-ping", () => Results.Ok());
        var before = ModuleEndpointRegistrar.CaptureEndpointSnapshot(app);

        var group = new ModuleEndpointRegistrar(app, "sample").MapGroup("/modules/sample");
        group.MapGet("/ping", () => Results.Ok());

        Should.NotThrow(() => ModuleEndpointRegistrar.VerifyModuleEndpoints(app, "sample", before));
    }
}
