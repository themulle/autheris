using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>SEC-PROC-01: the procedure kill switch (<c>SqlEndpoints:Procedures:Enabled</c>) fails closed and defaults to off.</summary>
public sealed class ProcedureKillSwitchTests
{
    private static GatewayOptions On() => new() { SqlEndpoints = new SqlEndpointsOptions { Procedures = new ProcedureEndpointsOptions { Enabled = true } } };

    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IServiceProvider ServiceProvider => services;
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
    }

    private static List<RouteEndpoint> Routes(GatewayOptions? options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()));
        services.AddSingleton(Substitute.For<IProcedureRegistry>());
        services.AddSingleton(Substitute.For<IProcedureExecutionService>());
        var builder = new RouteBuilder(services.BuildServiceProvider());
        builder.MapProcedureEndpoints(options);
        return builder.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Default_IsOff_WhenKeyAbsent()
    {
        var bound = new ConfigurationBuilder().Build().Get<GatewayOptions>() ?? new GatewayOptions();

        bound.SqlEndpoints.Procedures.Enabled.ShouldBeFalse();
        ReadPathSwitches.From(bound).ProceduresEnabled.ShouldBeFalse();
        ReadPathSwitches.From(new GatewayOptions()).ProceduresEnabled.ShouldBeFalse();
        ReadPathSwitches.From(On()).ProceduresEnabled.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Disabled_ProcedureRoutes_Return404_ForAdminAndAnonymous()
    {
        // No endpoint is mapped, so every caller (admin or anonymous) gets the router's 404 for list, openapi.json, GET and POST {name}.
        Routes(new GatewayOptions()).ShouldBeEmpty();
        Routes(null).ShouldBeEmpty();
    }

    [Fact]
    public void Enabled_RoutesMappedWithRestIngressTransport()
    {
        var routes = Routes(On());

        routes.Count.ShouldBe(4);
        routes.ShouldAllBe(r => r.RoutePattern.RawText!.StartsWith("/api/v1/procedures", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Disabled_NoProcedureServicesRegistered()
    {
        var off = new ServiceCollection().AddAutherisSqlEngine(new GatewayOptions());
        var on = new ServiceCollection().AddAutherisSqlEngine(On());

        off.Where(d => d.ServiceType.Name.Contains("Procedure", StringComparison.Ordinal)
                       || (d.ImplementationType?.Name.Contains("Procedure", StringComparison.Ordinal) ?? false))
            .ShouldBeEmpty();
        on.ShouldContain(d => d.ServiceType == typeof(IProcedureRegistry));
        on.ShouldContain(d => d.ServiceType == typeof(IProcedureExecutionService));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Disabled_CallProcedureTool_NotListed_AndCallIsUnknownTool()
    {
        var off = ReadPathSwitches.From(new GatewayOptions());
        var on = ReadPathSwitches.From(On());

        McpDatasetTools.Definitions(off).ShouldNotContain(t => t.Name == McpDatasetTools.CallProcedure);
        McpDatasetTools.Definitions(on).ShouldContain(t => t.Name == McpDatasetTools.CallProcedure);
        new McpToolRegistry(null, null, off).FindTool(McpDatasetTools.CallProcedure).ShouldBeNull();
        new McpToolRegistry(null, null, on).FindTool(McpDatasetTools.CallProcedure).ShouldNotBeNull();

        // A handler without procedure services answers like an unknown tool, never NOT_AVAILABLE, before looking at any argument.
        var handler = new McpToolExecutionHandler(
            Substitute.For<Autheris.Application.Sql.Interfaces.IGovernedSqlExecutionService>(),
            Substitute.For<Autheris.Application.DataSources.Interfaces.IGovernedDataQueryService>(),
            Substitute.For<Autheris.Application.Catalog.Interfaces.ICatalogDiscoveryService>(),
            Substitute.For<Autheris.Application.Mcp.Interfaces.IApiDispatcherService>(),
            Substitute.For<Autheris.Application.Lineage.ILineageGraphStore>(),
            Substitute.For<Autheris.Application.Common.Interfaces.ITableMetadataRepository>());
        var tool = McpDatasetTools.Definitions().Single(t => t.Name == McpDatasetTools.CallProcedure);
        var session = new McpSessionContext("s", "u", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await handler.ExecuteToolAsync(tool, "{}", session, CancellationToken.None);

        result.ShouldContain("is not registered or allowed");
        result.ShouldNotContain("NOT_AVAILABLE");
        result.ShouldNotContain("INVALID_PARAMS");
    }
}
