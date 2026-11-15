namespace Autheris.Tests.Integration.Removal;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>
/// Verifies at ASP.NET Core route-table level that removed authorization models register zero endpoints.
/// </summary>
[Trait("Category", "Security")]
public sealed class RemovedAuthzRoutesTests
{
    [Fact]
    public void RebacRoutes_AreNotMappedInEndpointRouting()
    {
        var mappedRoutes = EnumerateMappedRoutes();
        mappedRoutes.ShouldNotBeEmpty();
        mappedRoutes.ShouldNotContain(pattern => pattern.Contains("/rebac", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> EnumerateMappedRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=localhost;Port=5432;Database=autheris_unit;Username=autheris;Password=test",
            ["Gateway:OData:Enabled"] = "true",
            ["Gateway:Arrow:Enabled"] = "true",
            ["Gateway:DuckDbOlap:Enabled"] = "true",
            ["Gateway:Mcp:Enabled"] = "true",
            ["Gateway:SqlEndpoints:Enabled"] = "false",
            ["Gateway:Plugins:Directory"] = string.Empty
        });

        var devReport = DevConfiguration.Apply(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(devReport);

        builder.Services.AddGatewayTelemetry(builder.Configuration);
        builder.Services.AddDataSourceHierarchicalConfiguration(builder.Configuration);
        var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
        builder.Services.AddGatewayInfrastructure(gatewayOptions, builder.Environment);
        builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
        var demoDataEnabled = DemoDataSwitch.Resolve(gatewayOptions, builder.Environment.EnvironmentName);
        builder.Services.AddSingleton<IDemoDataSwitch>(new DemoDataSwitch(demoDataEnabled));
        builder.Services.AddGatewayGraphQL(gatewayOptions);
        builder.Services.AddAsyncJobsAndDatasourceTesting(builder.Configuration);
        using var bootstrapLoggerFactory = LoggerFactory.Create(_ => { });
        builder.Services.AddGatewayModules(builder.Configuration, builder.Environment, gatewayOptions, bootstrapLoggerFactory);

        using var app = builder.Build();
        app.UseGatewayPipeline(gatewayOptions);
        app.MapGatewayEndpoints(gatewayOptions);

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(ep => ep.RoutePattern.RawText ?? string.Empty)
            .ToList();
    }
}
