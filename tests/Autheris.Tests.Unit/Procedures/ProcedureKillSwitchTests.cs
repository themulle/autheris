using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
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
        services.AddSingleton(Substitute.For<IProcedureCatalog>());
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

    [Fact]
    public async Task Disabled_CatalogAndMcpDatasets_ListNoProcedures()
    {
        // Without procedure services (switch off) the dataset list holds no procedure entry and the REST catalog has no procedure detail.
        var tables = Autheris.Tests.Unit.Catalog.Wp0.Tables();
        var consents = Autheris.Tests.Unit.Catalog.Wp0.Consents();
        var user = Autheris.Tests.Unit.Catalog.Wp0.User("S-1", "tenant-a");
        var mcp = new McpDatasetCatalog(tables, consents, Substitute.For<Autheris.Application.Common.Interfaces.IGatewayExecutionService>(),
            Substitute.For<Autheris.Application.Common.Interfaces.IAuditLogRepository>(), options: Microsoft.Extensions.Options.Options.Create(new GatewayOptions()));
        var rest = new Autheris.Application.Catalog.Services.CatalogDiscoveryService(tables, Substitute.For<Autheris.Application.Common.Interfaces.IAuditLogRepository>(),
            Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), null);

        var list = await mcp.ListDatasetsAsync(user, null, null, CancellationToken.None);
        var detail = await rest.GetDatasetDetailAsync(new Autheris.Domain.Common.TableIdentifier("procedures", "procedure", "get_orders"),
            new RequestContext(new TenantId("tenant-a"), new Sid("S-1"), Roles: []), CancellationToken.None);

        list.Datasets.ShouldNotContain(d => d.Id.StartsWith("procedure", StringComparison.OrdinalIgnoreCase));
        list.Endpoints.ShouldNotBeNull();
        list.Endpoints!.ShouldNotContain(e => e.Url.Contains("/procedures", StringComparison.Ordinal));
        detail.ShouldBeNull();
    }

    [Fact]
    public async Task Disabled_OpenApiHasNoProcedureLink()
    {
        var repo = Substitute.For<Autheris.Application.Common.Interfaces.ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([]));
        var off = new Autheris.Application.OData.Services.DynamicOpenApiGenerator(repo, Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Application.OData.Services.DynamicOpenApiGenerator>.Instance);
        var on = new Autheris.Application.OData.Services.DynamicOpenApiGenerator(repo, Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Application.OData.Services.DynamicOpenApiGenerator>.Instance,
            new OpenApiDocumentOptions { IncludeStoredProcedureSpec = true });

        (await off.GetIndexDocumentAsync()).Apis.ShouldNotContain(a => a.Url.Contains("/procedures/", StringComparison.Ordinal));
        (await on.GetIndexDocumentAsync()).Apis.ShouldContain(a => a.Url.Contains("/procedures/", StringComparison.Ordinal));
    }
}
