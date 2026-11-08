namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Hosting;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleHostTests
{
    [Fact]
    public void Disabled_module_assembly_is_not_loaded()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Directory"] = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"),
            })
            .Build();
        var env = new FakeHostEnvironment();
        var connFactory = new GovernanceConnectionFactory(services.BuildServiceProvider());
        var beforeAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = ModuleLoader.LoadModules(services, config, env, connFactory, NullLogger<ModuleLoader>.Instance);
        result.Modules.ShouldBeEmpty();

        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Where(name =>
                !beforeAssemblies.Contains(name) &&
                name.StartsWith("Autheris.Modules.", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "Autheris.Modules.Abstractions", StringComparison.OrdinalIgnoreCase))
            .ToList();

        loadedAssemblies.ShouldBeEmpty();
    }
}

[Trait("Category", "Security")]
public sealed class ModuleConfigurationTests
{
    [Fact]
    public void Config_for_unloaded_known_module_fails_with_clear_message()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:AiGateway:Enabled"] = "true",
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.Validate(
                config,
                loadedModuleIds: Array.Empty<string>(),
                claimedModuleSections: Array.Empty<string>()));

        ex.Message.ShouldContain("Gateway:AiGateway");
        ex.Message.ShouldContain("module ai-gateway is not installed");
    }

    [Fact]
    public void Loaded_module_section_is_accepted_by_UnknownGatewayKeyGuard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:AiGateway:Enabled"] = "true",
                ["Gateway:SampleModule:Endpoint"] = "https://sample.internal",
            })
            .Build();

        Should.NotThrow(() =>
            UnknownGatewayKeyGuard.Validate(
                config,
                loadedModuleIds: ["ai-gateway", "sample"],
                claimedModuleSections: ["Gateway:AiGateway", "Gateway:SampleModule"]));
    }

    [Fact]
    public void Unknown_section_not_claimed_by_any_module_is_rejected()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:CompletelyUnknownSection:Foo"] = "bar",
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.Validate(
                config,
                loadedModuleIds: ["sample"],
                claimedModuleSections: ["Gateway:SampleModule"]));

        ex.Message.ShouldContain("Gateway:CompletelyUnknownSection");
    }
}

[Trait("Category", "Security")]
public sealed class ModuleEndpointTests
{
    private sealed class ConfigurableRateLimiter : IRateLimiterService
    {
        public bool AllowPostAuth { get; set; } = true;

        public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default) =>
            Task.FromResult(new RateLimitResult(true, 0));

        public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default) =>
            Task.FromResult(new RateLimitResult(AllowPostAuth, AllowPostAuth ? 0 : 30));

        public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            Task.FromResult(new CostQuotaResult(true, 100, 0));

        public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            Task.FromResult(new CostQuotaResult(true, 100, 0));

        public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            Task.FromResult(new CostQuotaResult(true, 100, 0));

        public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            Task.FromResult<CallerBucketState?>(null);

        public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<CallerBucketState?>(null);
    }

    private static (FakeEndpointRouteBuilder App, Endpoint PingEndpoint, Endpoint AdminEndpoint) BuildSampleModuleApp(
        FakeAuditWriter? auditWriter = null,
        IRateLimiterService? rateLimiter = null)
    {
        var services = new ServiceCollection();
        if (auditWriter is not null)
        {
            services.AddSingleton<IAuditWriter>(auditWriter);
        }

        if (rateLimiter is not null)
        {
            services.AddSingleton<IRateLimiterService>(rateLimiter);
        }

        var app = new FakeEndpointRouteBuilder(services);
        var registrar = new ModuleEndpointRegistrar(app, "sample");
        var module = new SampleValidModule();
        module.MapEndpoints(registrar);
        ModuleEndpointRegistrar.VerifyModuleEndpoints(app, "sample");

        var endpoints = app.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        var pingEndpoint = endpoints.Single(e => e.RoutePattern.RawText == "/api/v1/sample/ping");
        var adminEndpoint = endpoints.Single(e => e.RoutePattern.RawText == "/api/v1/sample/admin");

        return (app, pingEndpoint, adminEndpoint);
    }

    private static async Task<int> InvokeEndpointAsync(
        FakeEndpointRouteBuilder app,
        Endpoint endpoint,
        ClaimsPrincipal user,
        string path)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = app.ServiceProvider,
            User = user,
        };
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(context);
        return context.Response.StatusCode;
    }

    private static ClaimsPrincipal CreateAuthenticatedPrincipal(string sid = "S-1-5-21-900", string? role = null, string? tenantId = "tenant-a")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
        };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    [Fact]
    public async Task Unauthenticated_request_to_module_endpoint_returns_401()
    {
        var (app, pingEndpoint, _) = BuildSampleModuleApp();
        await using (app)
        {
            var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());
            var statusCode = await InvokeEndpointAsync(app, pingEndpoint, anonymousUser, "/api/v1/sample/ping");
            statusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        }
    }

    [Fact]
    public async Task Unauthorized_role_returns_403()
    {
        var (app, _, adminEndpoint) = BuildSampleModuleApp();
        await using (app)
        {
            var nonAdminUser = CreateAuthenticatedPrincipal(role: "Viewer");
            var statusCode = await InvokeEndpointAsync(app, adminEndpoint, nonAdminUser, "/api/v1/sample/admin");
            statusCode.ShouldBe(StatusCodes.Status403Forbidden);
        }
    }

    [Fact]
    public async Task Module_endpoint_emits_audit_entry()
    {
        var auditWriter = new FakeAuditWriter();
        var (app, pingEndpoint, _) = BuildSampleModuleApp(auditWriter: auditWriter);
        await using (app)
        {
            var user = CreateAuthenticatedPrincipal(sid: "S-1-5-21-777", tenantId: "tenant-audit");
            var statusCode = await InvokeEndpointAsync(app, pingEndpoint, user, "/api/v1/sample/ping");

            statusCode.ShouldBe(StatusCodes.Status200OK);
            auditWriter.Entries.Count.ShouldBe(1);
            auditWriter.Entries[0].ActorSid.Value.ShouldBe("S-1-5-21-777");
            auditWriter.Entries[0].TenantId.Value.ShouldBe("tenant-audit");
            auditWriter.Entries[0].Decision.ShouldBe("ALLOW");
        }
    }

    [Fact]
    public async Task Module_endpoint_is_rate_limited()
    {
        var rateLimiter = new ConfigurableRateLimiter { AllowPostAuth = false };
        var (app, pingEndpoint, _) = BuildSampleModuleApp(rateLimiter: rateLimiter);
        await using (app)
        {
            var user = CreateAuthenticatedPrincipal();
            var statusCode = await InvokeEndpointAsync(app, pingEndpoint, user, "/api/v1/sample/ping");
            statusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        }
    }

    [Fact]
    public async Task Tenant_context_is_enforced()
    {
        var (app, pingEndpoint, _) = BuildSampleModuleApp();
        await using (app)
        {
            var userWithoutTenant = CreateAuthenticatedPrincipal(tenantId: null);
            var deniedStatus = await InvokeEndpointAsync(app, pingEndpoint, userWithoutTenant, "/api/v1/sample/ping");
            deniedStatus.ShouldBe(StatusCodes.Status403Forbidden);

            var userWithTenant = CreateAuthenticatedPrincipal(tenantId: "tenant-verified");
            var allowedStatus = await InvokeEndpointAsync(app, pingEndpoint, userWithTenant, "/api/v1/sample/ping");
            allowedStatus.ShouldBe(StatusCodes.Status200OK);
        }
    }
}

[Trait("Category", "Security")]
public sealed class ModuleGovernanceDbIsolationTests
{
    [Theory]
    [InlineData("Gateway:GovernanceDb")]
    [InlineData("Gateway:GovernanceDb:ConnectionString")]
    [InlineData("gateway:governancedb:provider")]
    public void Module_cannot_override_governance_db(string claimedSection)
    {
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:GovernanceDb:ConnectionString"] = "Host=secret-host;Password=secret"
            })
            .Build();

        var method = typeof(ModuleLoader).GetMethod(
            "BuildScopedModuleConfiguration",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var ex = Should.Throw<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(null, ["mod", new[] { claimedSection }, root]));
        ex.InnerException.ShouldBeOfType<ModuleLoadException>();
        ex.InnerException!.Message.ShouldNotContain("secret");
    }
}
