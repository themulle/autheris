namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Policy;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-AZ-08: keys of the removed relationship-tuple authorization model fail startup with the replacement message (never silent no-ops),
/// and its routes and types no longer exist, so every caller gets the router's 404.
/// </summary>
[Trait("Category", "Security")]
public sealed class RemovedAuthzKeysTests
{
    private const string ReplacementMessage = "removed by ADR-020; data access is granted by grant records";

    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static string StartupFailure(string key, string? value)
    {
        var configuration = Config(key, value);
        return Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration)).Message;
    }

    [Theory]
    [InlineData("Gateway:Rebac:Enabled", "false")]
    [InlineData("Gateway:Rebac:Enabled", "true")]
    [InlineData("Gateway:Rebac:EnforceOnQueryPath", "")]
    [InlineData("Gateway:Rebac:SeedTuples:0:Subject", "user:alice")]
    public void RebacSection_FailsStartup(string key, string value)
    {
        var message = StartupFailure(key, value);

        message.ShouldContain("Gateway:Rebac");
        message.ShouldContain(ReplacementMessage);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void DangerBypassRebac_FailsStartup(string value)
    {
        var message = StartupFailure("Gateway:Insecure:danger_bypass_rebac", value);

        message.ShouldContain("Gateway:Insecure:danger_bypass_rebac");
        message.ShouldContain(ReplacementMessage);
    }

    [Fact]
    public void RebacRoutes_Return404()
    {
        // Nothing maps /api/v1/rebac/*: the endpoint group, its filter, its store and its options type are gone, so the router answers 404.
        var assemblies = new[]
        {
            typeof(UnknownGatewayKeyGuard).Assembly,
            typeof(GatewayOptions).Assembly,
            typeof(Autheris.Application.Policy.Pdp.IAccessDecisionPoint).Assembly,
            typeof(Autheris.GraphQL.Directives.McpToolDirectiveType).Assembly,
            typeof(Autheris.Infrastructure.Cache.ConsentCacheService).Assembly,
        };
        var leftovers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.Contains("Rebac", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToList();
        leftovers.ShouldBeEmpty();

        typeof(GatewayOptions).GetProperty("Rebac").ShouldBeNull();
        typeof(GatewayOptions).GetProperty("IsRebacBypassed").ShouldBeNull();
        var endpointMethods = typeof(UnknownGatewayKeyGuard).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("Rebac", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.DeclaringType!.Name + "." + m.Name)
            .ToList();
        endpointMethods.ShouldBeEmpty();

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

    [Fact]
    public void Rebac_and_casbin_keys_fail_startup_and_routes_404()
    {
        // ReBAC rows only; the Casbin rows are appended when the Casbin engine is removed.
        foreach (var (key, value) in new[]
                 {
                     ("Gateway:Rebac:Enabled", "true"),
                     ("Gateway:Insecure:danger_bypass_rebac", "true"),
                 })
        {
            StartupFailure(key, value).ShouldContain(ReplacementMessage);
        }

        RebacRoutes_Return404();
    }
}
