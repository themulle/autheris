namespace Autheris.Modules.Lakehouse.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.Modules.Lakehouse;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class LakehouseModuleContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LakehouseModuleContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing Autheris.sln.");
    }

    [Fact]
    public void Iceberg_routes_unchanged_when_enabled()
    {
        using var app = new ContractEndpointRouteBuilder();
        var registrar = new ModuleEndpointRegistrar(app, "lakehouse");
        var module = new LakehouseModule();

        module.MapEndpoints(registrar);
        ModuleEndpointRegistrar.VerifyModuleEndpoints(app, "lakehouse");

        var actualRoutes = app.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId == "lakehouse")
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
                return methods.Select(m => $"{m} {e.RoutePattern.RawText}");
            })
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        var baselinePath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Autheris.Tests.Architecture",
            "Baselines",
            "lakehouse-routes.txt");
        File.Exists(baselinePath).ShouldBeTrue($"Missing route baseline at '{baselinePath}'.");

        var expectedRoutes = File.ReadAllLines(baselinePath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        actualRoutes.ShouldBe(expectedRoutes);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Iceberg_routes_require_authorization()
    {
        using var app = new ContractEndpointRouteBuilder();
        var registrar = new ModuleEndpointRegistrar(app, "lakehouse");
        var module = new LakehouseModule();

        module.MapEndpoints(registrar);
        ModuleEndpointRegistrar.VerifyModuleEndpoints(app, "lakehouse");

        var endpoints = app.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId == "lakehouse")
            .ToList();

        endpoints.Count.ShouldBe(4);

        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();
            endpoint.Metadata.GetMetadata<AuditPolicy>().ShouldNotBeNull();
            endpoint.Metadata.GetMetadata<IngressTransportMetadata>().ShouldNotBeNull();

            var context = new DefaultHttpContext
            {
                RequestServices = app.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity())
            };
            context.Request.Path = "/v1/tenant-a/namespaces";
            context.Request.Method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? HttpMethods.Get;
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(endpoint);

            await endpoint.RequestDelegate!(context);
            context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Iceberg_routes_404_when_absent()
    {
        await using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-lh-absent-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:Tenancy:DefaultTenantId", "tenant-1");
        });

        using var client = host.CreateClient();
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/v1/tenant-1/namespaces"),
            (HttpMethod.Get, "/v1/tenant-1/namespaces/raw/tables"),
            (HttpMethod.Get, "/v1/tenant-1/namespaces/raw/tables/orders"),
            (HttpMethod.Post, "/v1/tenant-1/namespaces/raw/tables/orders/credentials")
        };

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Test-User-Sid", "S-1-5-21-LAKEHOUSE-CONTRACT");
            request.Headers.Add("X-Test-Roles", "GovernanceAdmin");
            request.Headers.Add("X-Test-Tenant-Id", "tenant-1");

            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Expected 404 when Lakehouse module is not enabled for {method} {path}.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lakehouse_config_without_module_fails_fast()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Lakehouse:Enabled"] = "true",
                ["Gateway:Lakehouse:Storage:Provider"] = "Local"
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.Validate(
                config,
                loadedModuleIds: Array.Empty<string>(),
                claimedModuleSections: Array.Empty<string>()));

        ex.Message.ShouldContain("Gateway:Lakehouse");
        ex.Message.ShouldContain("lakehouse");
        ex.Message.ShouldContain("Autheris.Modules.Lakehouse");

        var services = new ServiceCollection();
        var env = new ContractHostEnvironment { EnvironmentName = Environments.Development };
        var hostEx = Should.Throw<InvalidOperationException>(() =>
            services.AddGatewayModules(config, env, new GatewayOptions()));
        hostEx.Message.ShouldContain("Autheris.Modules.Lakehouse");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Delta_executor_enforces_query_gate()
    {
        var reader = new RecordingDeltaMetadataReader();
        var executor = new DeltaLakeDataSourceExecutor(
            reader,
            new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance),
            new NoopColumnMaskingProvider(),
            Options.Create(new GatewayOptions()),
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var tableId = new TableIdentifier("lake", "sales", "delta_orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SourceName = "lake",
                SchemaName = "sales",
                TableName = "delta_orders",
                DataSourceType = DataSourceType.LakehouseDelta
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "string" },
                new TableColumn { ColumnName = "tenantId", DataType = "string" }
            ]
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "analyst@corp.com")], "Bearer"));

        // 1. Access decision denied -> empty result without touching the Delta log
        var deniedContext = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Denied(tableId, "Denied by policy"),
            new Dictionary<string, object?>(),
            ["id"],
            null,
            100,
            0,
            new TenantId("tenant-1"));

        var deniedRows = await executor.ExecuteAsync(deniedContext);
        deniedRows.ShouldBeEmpty();
        reader.LoadCalls.ShouldBe(0);

        // 2. Missing tenant -> empty result without touching the Delta log
        var missingTenantContext = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["tenantId"] = ColumnAccessLevel.Clear
            }),
            new Dictionary<string, object?>(),
            ["id"],
            null,
            100,
            0,
            null);

        var missingTenantRows = await executor.ExecuteAsync(missingTenantContext);
        missingTenantRows.ShouldBeEmpty();
        reader.LoadCalls.ShouldBe(0);

        // 3. Uncataloged filter predicate -> throws GatewaySecurityException before reading Delta log
        var uncatalogedFilterContext = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["tenantId"] = ColumnAccessLevel.Clear
            }),
            new Dictionary<string, object?> { ["undeclared_col"] = "secret" },
            ["id"],
            null,
            100,
            0,
            new TenantId("tenant-1"));

        await Should.ThrowAsync<GatewaySecurityException>(() => executor.ExecuteAsync(uncatalogedFilterContext));
        reader.LoadCalls.ShouldBe(0);
    }
private sealed class ContractEndpointRouteBuilder : IEndpointRouteBuilder, IDisposable
    {
        private readonly ServiceProvider _provider;

        public ContractEndpointRouteBuilder()
        {
            var services = new ServiceCollection();
            services.AddRouting();
            services.AddLogging();
            services.AddAuthorization();
            services.AddSingleton(NSubstitute.Substitute.For<IIcebergRestCatalogFederationService>());
            _provider = services.BuildServiceProvider();
            ServiceProvider = _provider;
        }

        public IServiceProvider ServiceProvider { get; }

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

        public void Dispose() => _provider.Dispose();
    }

    private sealed class ContractHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Autheris.Modules.Lakehouse.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingDeltaMetadataReader : IDeltaMetadataReader
    {
        public int LoadCalls { get; private set; }

        public ValueTask<DeltaSnapshot> LoadSnapshotAsync(
            string tableLocation,
            long? asOfVersion = null,
            DateTimeOffset? asOfTimestamp = null,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            throw new InvalidOperationException("Delta log should not be read when the query gate rejects the request.");
        }

        public ValueTask<IReadOnlyList<DeltaDataFile>> LoadActiveFilesAsync(
            string tableLocation,
            long? asOfVersion = null,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            throw new InvalidOperationException("Delta log should not be read when the query gate rejects the request.");
        }
    }

    private sealed class NoopColumnMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => rawValue;
    }

    
}
