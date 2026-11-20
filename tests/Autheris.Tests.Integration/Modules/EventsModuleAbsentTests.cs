namespace Autheris.Tests.Integration.Modules;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>Host behaviour without the events module: its routes and keys fail closed, the core consent webhook path keeps working.</summary>
[Trait("Category", "Security")]
public sealed class EventsModuleAbsentTests : IClassFixture<EventsModuleAbsentTests.AbsentHost>, IClassFixture<EventsModuleAbsentTests.ModuleHost>
{
    private const string DebeziumBody = """
        {
            "op": "c",
            "source": { "schema": "crm", "table": "customers", "name": "salesdb" },
            "after": { "id": 200, "name": "Ingested", "tenant_id": "tenant-sales" }
        }
        """;

    private readonly AbsentHost _absent;
    private readonly ModuleHost _withModule;

    public EventsModuleAbsentTests(AbsentHost absent, ModuleHost withModule)
    {
        _absent = absent;
        _withModule = withModule;
    }

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-EVENTS-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin,ClusterAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant-Id", "tenant-sales");
        return client;
    }

    [Fact]
    public async Task Cdc_stream_routes_return_404_without_module()
    {
        using var client = Admin(_absent);
        var routes = new (HttpMethod Method, string Path)[]
        {
            // New and old paths: the old API routes are gone for good, the module routes exist only with the module.
            (HttpMethod.Post, "/modules/events/v1/cdc/events"),
            (HttpMethod.Get, "/modules/events/v1/cdc/streams"),
            (HttpMethod.Get, "/modules/events/v1/cdc/subscriptions"),
            (HttpMethod.Post, "/modules/events/v1/cdc/streams/0123456789abcdef0123456789abcdef/resync"),
            (HttpMethod.Post, "/api/v1/cdc/events"),
            (HttpMethod.Get, "/api/v1/cdc/streams"),
            (HttpMethod.Get, "/api/v1/cdc/subscriptions"),
            (HttpMethod.Delete, "/api/v1/cdc/subscriptions/some-id"),
        };

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(DebeziumBody, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path} must be 404 without the events module.");
        }

        // Anonymous callers never see a 401 on a route that does not exist.
        using var anonymous = _absent.CreateClient();
        using var anonymousResponse = await anonymous.PostAsync("/modules/events/v1/cdc/events", new StringContent(DebeziumBody, Encoding.UTF8, "application/json"));
        anonymousResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Events_config_section_fails_fast_without_module()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("EventsConfigAbsent"),
            ["Gateway:Events:CdcWebhooks:Enabled"] = "true",
        };
        using var host = new IntegrationHost(settings);

        var ex = Should.Throw<Exception>(() => host.CreateClient());
        ex.ToString().ShouldContain("Gateway:Events");
        ex.ToString().ShouldContain("module events is not installed");

        // The removed key names its new path.
        var removed = new Dictionary<string, string?>(settings) { ["Gateway:" + "CdcWebhooks:Enabled"] = "true" };
        removed.Remove("Gateway:Events:CdcWebhooks:Enabled");
        using var removedHost = new IntegrationHost(removed);
        Should.Throw<Exception>(() => removedHost.CreateClient()).ToString().ShouldContain("Gateway:Events:CdcWebhooks");
    }

    [Fact]
    public void Webhook_subscriptions_without_events_module_fail_fast()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("WebhookSubscriptionsAbsent"),
            ["Gateway:Webhooks:Subscriptions:0:Id"] = "hook-1",
            ["Gateway:Webhooks:Subscriptions:0:TargetUrl"] = "https://93.184.216.34/hook",
            ["Gateway:Webhooks:Subscriptions:0:EventFilters:0"] = "*",
        };
        using var host = new IntegrationHost(settings);

        var ex = Should.Throw<Exception>(() => host.CreateClient());
        ex.ToString().ShouldContain("Gateway:Webhooks:Subscriptions");
        ex.ToString().ShouldContain("events");
    }

    [Fact]
    public void Consent_webhook_dispatch_works_without_events_module()
    {
        // The consent dispatcher signs and delivers through the core primitives only: it, the delivery client, the retry policy and
        // the secret protector are host services, and no events-module worker is present.
        var services = _absent.Services;
        var hosted = services.GetServices<IHostedService>().ToList();

        hosted.ShouldContain(h => h is ConsentWebhookDispatcherHostedService);
        hosted.ShouldNotContain(h => h is CdcWebhookDeliveryService);
        hosted.ShouldNotContain(h => h.GetType().Assembly.GetName().Name == "Autheris.Modules.Events");
        services.GetRequiredService<WebhookDeliveryClient>().ShouldNotBeNull();
        services.GetRequiredService<WebhookRetryPolicy>().ShouldNotBeNull();
        services.GetService<IWebhookSecretProtector>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Cdc_ingest_requires_auth_and_audits_under_module()
    {
        using var anonymous = _withModule.CreateClient();
        using var denied = await anonymous.PostAsync("/modules/events/v1/cdc/events", new StringContent(DebeziumBody, Encoding.UTF8, "application/json"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = Admin(_withModule);
        using var accepted = await client.PostAsync("/modules/events/v1/cdc/events", new StringContent(DebeziumBody, Encoding.UTF8, "application/json"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var scope = _withModule.Services.CreateScope();
        var entries = (await scope.ServiceProvider.GetRequiredService<IAuditLogRepository>().GetAuditLogEntriesAsync(1000)).ToArray();
        entries.ShouldContain(e => e.EventType == AuditEventTypes.StreamSubscribe);

        // The old path stays gone with the module loaded.
        using var legacy = await client.PostAsync("/api/v1/cdc/events", new StringContent(DebeziumBody, Encoding.UTF8, "application/json"));
        legacy.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public sealed class AbsentHost : IntegrationHost<AbsentHost>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("EventsModuleAbsentTests"),
            });
        }
    }

    public sealed class ModuleHost : IntegrationHost<ModuleHost>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("EventsModuleLoadedTests"),
            };
            EventsWorkspaceTestSettings.Apply((k, v) => settings[k] = v);
            config.AddInMemoryCollection(settings);
        }
    }
}
