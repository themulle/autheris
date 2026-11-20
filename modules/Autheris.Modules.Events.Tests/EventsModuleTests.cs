namespace Autheris.Modules.Events.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Hosting;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class EventsModuleTests
{
    private const string AdminPolicy = "GovernanceAdmin";

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

    private static List<RouteEndpoint> MappedEndpoints(out ContractEndpointRouteBuilder app)
    {
        app = new ContractEndpointRouteBuilder();
        new EventsModule().MapEndpoints(new ModuleEndpointRegistrar(app, "events"));
        ModuleEndpointRegistrar.VerifyModuleEndpoints(app, "events");
        return app.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId == "events")
            .ToList();
    }

    private static string Route(RouteEndpoint e) =>
        $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}";

    [Fact]
    public void Module_exposes_the_eight_cdc_routes_only_under_the_module_prefix()
    {
        var endpoints = MappedEndpoints(out var app);
        using (app)
        {
            endpoints.Select(Route).Order(StringComparer.Ordinal).ToArray().ShouldBe(
            [
                "DELETE /modules/events/v1/cdc/subscriptions/{id}",
                "GET /modules/events/v1/cdc/streams",
                "GET /modules/events/v1/cdc/subscriptions",
                "POST /modules/events/v1/cdc/events",
                "POST /modules/events/v1/cdc/streams/{id}/reset-watermark",
                "POST /modules/events/v1/cdc/streams/{id}/resync",
                "POST /modules/events/v1/cdc/streams/{id}/rotate-image-key",
                "POST /modules/events/v1/cdc/subscriptions",
            ]);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Module_routes_require_authorization()
    {
        var endpoints = MappedEndpoints(out var app);
        using (app)
        {
            endpoints.Count.ShouldBe(8);
            foreach (var endpoint in endpoints)
            {
                endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();
                endpoint.Metadata.GetMetadata<AuditPolicy>().ShouldNotBeNull();
                endpoint.Metadata.GetMetadata<IngressTransportMetadata>().ShouldNotBeNull();

                // Anonymous callers are refused by the combined policy of the route (default policy plus any per-route policy).
                var policy = await AuthorizationPolicy.CombineAsync(
                    app.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>(),
                    endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                policy.ShouldNotBeNull();
                var result = await app.ServiceProvider.GetRequiredService<IAuthorizationService>()
                    .AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), policy!);
                result.Succeeded.ShouldBeFalse(Route(endpoint));
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Cdc_admin_routes_keep_governance_admin_policy()
    {
        var endpoints = MappedEndpoints(out var app);
        using (app)
        {
            var admin = endpoints.Where(e => e.RoutePattern.RawText!.Contains("/cdc/streams", StringComparison.Ordinal)).ToList();
            admin.Count.ShouldBe(4);
            foreach (var endpoint in admin)
            {
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(a => a.Policy == AdminPolicy, Route(endpoint));
            }

            // The ingestion route is not exempt: it carries the registrar audit, the ingress transport and the full-audit level.
            var ingest = endpoints.Single(e => e.RoutePattern.RawText!.EndsWith("/cdc/events", StringComparison.Ordinal));
            ingest.Metadata.GetMetadata<AuditPolicy>()!.Level.ShouldBe(AuditLevel.Full);
            ingest.Metadata.GetMetadata<IngressTransportMetadata>().ShouldNotBeNull();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Event_delivery_uses_core_webhook_delivery_client()
    {
        // The module registers no HttpClient of its own: delivery goes through the host WebhookDeliveryClient, whose chain is verified below.
        var services = new ServiceCollection();
        services.AddEventsModule(new ConfigurationBuilder().Build());

        services.Any(d => d.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>)).ShouldBeFalse();
        typeof(WebhookDeliveryClient).Assembly.GetName().Name.ShouldBe("Autheris.Application");
        typeof(WebhookDeliveryHostedService).GetConstructors().Single().GetParameters()
            .ShouldContain(p => p.ParameterType == typeof(WebhookDeliveryClient));
        typeof(Autheris.Application.Streaming.Services.CdcWebhookDeliveryService).Assembly.GetName().Name.ShouldBe("Autheris.Modules.Events");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Webhook_signature_secret_not_logged()
    {
        var log = new List<string>();
        var secret = "S3cr3t-" + new string('x', 40);
        var (response, _) = await CreateSubscriptionAndCollect(secret, log);

        log.ShouldNotContain(l => l.Contains(secret, StringComparison.Ordinal));
        response.ShouldNotContain(secret);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Webhook_signing_secret_not_in_logs_audit_or_responses()
    {
        var log = new List<string>();
        var secret = "S1gn1ng-" + new string('y', 40);
        var (response, storedSecret) = await CreateSubscriptionAndCollect(secret, log);

        response.ShouldNotContain(secret);
        log.ShouldNotContain(l => l.Contains(secret, StringComparison.Ordinal));
        // Stored only wrapped by the secret protector, never in plaintext.
        storedSecret.ShouldNotBe(secret);
        storedSecret.ShouldStartWith("enc:v1:");
        // The view of a subscription never carries a secret value (not even the ciphertext).
        Regex.IsMatch(response, "enc:v1:").ShouldBeFalse();
        // Audit details of the module carry no body or header values: the registrar builds them from route metadata only.
        AuditDetailsBuilder.IsRedactedKey("signingSecret").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("kekProvider").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("openLineageApiKey").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("api-key").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Events_module_has_no_data_source_or_governance_connection_reference()
    {
        var assembly = typeof(EventsModule).Assembly;
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        referenced.ShouldNotContain("Autheris.Infrastructure");
        referenced.ShouldNotContain("Autheris.Api");
        referenced.ShouldNotContain("Autheris.Extensions");

        // Built from parts so this file does not trip the repository-wide scan for the same identifiers.
        string[] forbidden =
        [
            "ISql" + "ConnectionFactory", "Sql" + "ConnectionFactory", "Npgsql" + "Connection", "Sql" + "Connection", "Oracle" + "Connection",
            "Sql" + "iteConnection", "DuckDB" + "Connection", "DatabricksStatement" + "Client", "Db" + "Connection", "Governance" + "Repository",
            "IGovernance" + "ConnectionFactory", "Command" + "Text",
        ];
        var root = Path.Combine(FindRepositoryRoot(), "modules", "Autheris.Modules.Events");
        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => forbidden
                .Where(t => Regex.IsMatch(File.ReadAllText(f), $@"\b{Regex.Escape(t)}\b"))
                .Select(t => $"{Path.GetFileName(f)}: {t}"))
            .ToList();
        violations.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Events_module_loads_under_the_service_guard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "events",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
                ["Gateway:Modules:WorkspacePath"] = Path.Combine(FindRepositoryRoot(), "modules", "Autheris.Modules.Events", "bin"),
            })
            .Build();
        var env = new ContractHostEnvironment { EnvironmentName = Environments.Development };
        var services = new ServiceCollection();

        var result = new ModuleLoader().LoadAndConfigureServices(services, config, env);

        result.LoadedModuleIds.ShouldBe(["events"]);
        // The module's own hosted services are accepted; the host descriptors are untouched.
        services.Count(d => d.ServiceType == typeof(IHostedService)).ShouldBeGreaterThanOrEqualTo(3);
    }

    private static async Task<(string Response, string StoredSecret)> CreateSubscriptionAndCollect(string secret, List<string> log)
    {
        var store = new Autheris.Application.Events.Services.InMemoryCloudEventSubscriptionStore();
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.IsAvailable.Returns(true);
        protector.Protect(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(c => "enc:v1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.ArgAt<string>(2)).Reverse().ToArray()));
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var services = new ServiceCollection();
        services.AddRequestContext();
        services.AddLogging(b => b.AddProvider(new ListLoggerProvider(log)).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IOptions<CdcWebhooksOptions>>(Options.Create(new CdcWebhooksOptions { Enabled = true }));
        services.AddSingleton<Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore>(store);
        services.AddSingleton(protector);
        services.AddSingleton(env);
        services.AddSingleton<IRestrictedScopePolicy>(RestrictedScopePolicy.Default);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Autheris.Domain.Common.Sid("S-1-5-21-ADMIN"),
            TenantId = new Autheris.Domain.Common.TenantId("tenant-a"),
            GroupSids = new HashSet<Autheris.Domain.Common.Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string> { "GovernanceAdmin" },
            AuthenticationScheme = "Test"
        };
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "admin"),
            new("tenant", "tenant-a"),
            new(ClaimTypes.Role, "GovernanceAdmin"),
            new(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
            Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-3003"),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var body = new Autheris.Domain.Model.CloudEventWebhookSubscription("c", "t", "https://93.184.216.34/hook", "public.orders", [], secret);
        var created = await Autheris.Api.Endpoints.StreamingCdcEndpoints.CreateSubscriptionAsync(context, body);
        var listed = await Autheris.Api.Endpoints.StreamingCdcEndpoints.ListSubscriptionsAsync(context);

        var stored = (await store.ListSubscriptionsAsync("tenant-a")).Single();
        return (Serialize(created) + Serialize(listed), stored.HmacSecret);
    }

    private static string Serialize(IResult result) =>
        System.Text.Json.JsonSerializer.Serialize(result.GetType().GetProperty("Value")?.GetValue(result));

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
        }
    }

    private sealed class ContractEndpointRouteBuilder : IEndpointRouteBuilder, IDisposable
    {
        private readonly ServiceProvider _provider;

        public ContractEndpointRouteBuilder()
        {
            var services = new ServiceCollection();
            services.AddRouting();
            services.AddLogging();
            services.AddAuthorization(o => o.AddPolicy(AdminPolicy, p => p.RequireRole(AdminPolicy)));
            services.AddRequestContext();
            // Registered so minimal-API parameter inference treats these as services (never resolved: anonymous calls stop at 401).
            services.AddSingleton<Autheris.Application.Streaming.Services.CdcStreamAdminService>(_ => null!);
            services.AddSingleton(Substitute.For<Autheris.Application.Audit.IAuditWriter>());
            services.AddSingleton(Substitute.For<Autheris.Application.Streaming.Interfaces.ICdcEventIngestionService>());
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

        public string ApplicationName { get; set; } = "Autheris.Modules.Events.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
