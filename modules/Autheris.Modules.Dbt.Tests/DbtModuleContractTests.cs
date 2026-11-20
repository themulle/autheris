namespace Autheris.Modules.Dbt.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Hosting;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

public sealed class DbtModuleContractTests
{
    private const long MiB = 1024 * 1024;

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

    private static List<RouteEndpoint> MapModule(ContractEndpointRouteBuilder app)
    {
        new DbtModule().MapEndpoints(new ModuleEndpointRegistrar(app, "dbt"));
        ModuleEndpointRegistrar.VerifyModuleEndpoints(app, "dbt");

        return app.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId == "dbt")
            .ToList();
    }

    private static string Describe(RouteEndpoint e)
    {
        var method = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() ?? "ANY";
        return $"{method} {e.RoutePattern.RawText}";
    }

    [Fact]
    public void Dbt_routes_unchanged_when_enabled()
    {
        using var app = new ContractEndpointRouteBuilder();
        var actualRoutes = MapModule(app).Select(Describe).OrderBy(r => r, StringComparer.Ordinal).ToArray();

        var baselinePath = Path.Combine(FindRepositoryRoot(), "tests", "Autheris.Tests.Architecture", "Baselines", "dbt-routes.txt");
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
    public async Task Dbt_routes_require_authorization()
    {
        using var app = new ContractEndpointRouteBuilder();
        var endpoints = MapModule(app);
        endpoints.Count.ShouldBe(12);

        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();
            endpoint.RoutePattern.RawText.ShouldNotBeNull().ShouldStartWith("/modules/dbt/");

            var context = new DefaultHttpContext
            {
                RequestServices = app.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity())
            };
            context.Request.Method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? HttpMethods.Get;
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(endpoint);

            await endpoint.RequestDelegate!(context);
            context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_module_maps_no_anonymous_route()
    {
        using var app = new ContractEndpointRouteBuilder();
        var endpoints = MapModule(app);
        endpoints.ShouldNotBeEmpty();

        endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null).ShouldBeEmpty();
        endpoints.Where(e => e.Metadata.GetMetadata<AuditExemption>() is not null).ShouldBeEmpty();
        endpoints.Any(e => e.RoutePattern.RawText!.Contains("webhook", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_endpoint_audit_levels_preserved()
    {
        var expected = new Dictionary<string, (AuditLevel Level, string EventType)>(StringComparer.Ordinal)
        {
            ["POST /modules/dbt/sync"] = (AuditLevel.Full, AuditEventTypes.AuditConfigChanged),
            ["POST /modules/dbt/governance"] = (AuditLevel.Full, AuditEventTypes.AuditConfigChanged),
            ["GET /modules/dbt/exposures"] = (AuditLevel.Summarized, AuditEventTypes.CatalogRead),
            ["GET /modules/dbt/proposals"] = (AuditLevel.Summarized, AuditEventTypes.CatalogRead),
            ["POST /modules/dbt/proposals/{id:guid}/approve"] = (AuditLevel.Full, AuditEventTypes.ConsentApproved),
            ["POST /modules/dbt/proposals/{id:guid}/reject"] = (AuditLevel.Full, AuditEventTypes.ConsentDenied),
            ["POST /modules/dbt/validate-contract"] = (AuditLevel.Summarized, AuditEventTypes.CatalogRead),
            ["POST /modules/dbt/run-results"] = (AuditLevel.Full, AuditEventTypes.AuditConfigChanged),
            ["GET /modules/dbt/health"] = (AuditLevel.Summarized, AuditEventTypes.CatalogRead),
            ["POST /modules/dbt/health/reset"] = (AuditLevel.Full, AuditEventTypes.AuditConfigChanged),
            ["POST /modules/dbt/semantic-manifest"] = (AuditLevel.Full, AuditEventTypes.AuditConfigChanged),
            ["GET /modules/dbt/metrics"] = (AuditLevel.Summarized, AuditEventTypes.CatalogRead),
        };

        using var app = new ContractEndpointRouteBuilder();
        var actual = MapModule(app).ToDictionary(
            Describe,
            e =>
            {
                var policy = e.Metadata.GetMetadata<AuditPolicy>().ShouldNotBeNull();
                return (policy.Level, policy.EventType);
            },
            StringComparer.Ordinal);

        actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).ShouldBe(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_body_limits_preserved()
    {
        var expected = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["POST /modules/dbt/sync"] = 100 * MiB,
            ["POST /modules/dbt/governance"] = 100 * MiB,
            ["POST /modules/dbt/validate-contract"] = 100 * MiB,
            ["POST /modules/dbt/run-results"] = 50 * MiB,
            ["POST /modules/dbt/semantic-manifest"] = 10 * MiB,
        };

        using var app = new ContractEndpointRouteBuilder();
        var actual = MapModule(app)
            .Select(e => (Route: Describe(e), Limit: e.Metadata.GetOrderedMetadata<IRequestSizeLimitMetadata>().LastOrDefault()?.MaxRequestBodySize))
            .Where(x => x.Limit is not null)
            .ToDictionary(x => x.Route, x => x.Limit!.Value, StringComparer.Ordinal);

        actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).ShouldBe(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_module_registers_owner_identity_verifier()
    {
        var services = new ServiceCollection();
        new DbtModule().ConfigureServices(services, new ConfigurationBuilder().Build(), new ContractHostContext());

        var verifier = services.Single(d => d.ServiceType == typeof(IDataOwnerIdentityVerifier));
        verifier.ImplementationType.ShouldBe(typeof(ScimDataOwnerIdentityVerifier));

        var handler = services.Single(d => d.ServiceType == typeof(IDbtCloudWebhookHandler));
        handler.ImplementationType.ShouldNotBeNull().Assembly.ShouldBe(typeof(DbtModule).Assembly);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_module_loads_under_the_service_guard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "dbt",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
                ["Gateway:Modules:WorkspacePath"] = Path.Combine(FindRepositoryRoot(), "modules", "Autheris.Modules.Dbt", "bin"),
            })
            .Build();
        var env = new ContractHostEnvironment { EnvironmentName = Environments.Development };

        var result = new ModuleLoader().LoadAndConfigureServices(new ServiceCollection(), config, env);

        result.LoadedModuleIds.ShouldBe(["dbt"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_config_without_module_fails_fast()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Dbt:Enabled"] = "true",
                ["Gateway:Dbt:WebhookSecret"] = "configured-secret"
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.Validate(
                config,
                loadedModuleIds: Array.Empty<string>(),
                claimedModuleSections: Array.Empty<string>()));

        ex.Message.ShouldContain("Gateway:Dbt");
        ex.Message.ShouldContain("dbt");
        ex.Message.ShouldContain("Autheris.Modules.Dbt");
    }

    private sealed class ContractHostContext : IModuleHostContext
    {
        public TimeProvider Time => TimeProvider.System;

        public string HostVersion => "test";
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
            services.AddSingleton<IModuleRequestContext, ModuleRequestContext>();
            services.AddSingleton(NSubstitute.Substitute.For<IDbtMetadataIngestionService>());
            services.AddSingleton(NSubstitute.Substitute.For<IDbtExposurePublisher>());
            services.AddSingleton(NSubstitute.Substitute.For<IDbtContractValidator>());
            services.AddSingleton(NSubstitute.Substitute.For<IDbtHealthCircuitBreaker>());
            services.AddSingleton(NSubstitute.Substitute.For<IDbtTestTargetMap>());
            services.AddSingleton(NSubstitute.Substitute.For<IDbtSemanticManifestIngestionService>());
            services.AddSingleton(NSubstitute.Substitute.For<ISemanticCatalogService>());
            services.AddSingleton(SemanticTenancy.FromOptions(null));
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
        public string ApplicationName { get; set; } = "Autheris.Modules.Dbt.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
