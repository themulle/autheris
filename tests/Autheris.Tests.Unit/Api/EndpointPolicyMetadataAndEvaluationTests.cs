namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EndpointPolicyMetadataAndEvaluationTests
{
    private static readonly HashSet<Type> ConcreteEndpointServices =
    [
        typeof(CallerAttributeAdministrationService),
        typeof(CallerAttributeSimulationService),
        typeof(VirtualFilterAdministrationService),
        typeof(DataProductService),
        typeof(ProductDiscoveryService),
        typeof(ProductSyncService),
        typeof(ProductSyncWebhookService),
        typeof(SqlEndpointLoader),
        typeof(TimeProvider)
    ];

    private static IReadOnlyList<RouteEndpoint> MapAndGetEndpoints(Action<IEndpointRouteBuilder> map)
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddLogging();
        services.AddAuthorization();
        using var innerProvider = services.BuildServiceProvider();
        var provider = new ServiceRecognizingProvider(innerProvider);
        var builder = new RouteInspectorBuilder(provider);
        map(builder);
        return builder.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static void AssertProtectedRoutePolicy(
        IReadOnlyList<RouteEndpoint> endpoints,
        string method,
        string rawPattern,
        string expectedPolicy)
    {
        var matches = endpoints.Where(e =>
            string.Equals(e.RoutePattern.RawText, rawPattern, StringComparison.Ordinal) &&
            (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) ?? false))
            .ToList();

        matches.Count.ShouldBe(1, $"Expected single route for {method} {rawPattern}, found {matches.Count}.");
        var endpoint = matches[0];
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull($"{method} {rawPattern} must not allow anonymous access.");

        var authMetadata = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        authMetadata.ShouldNotBeEmpty($"{method} {rawPattern} must carry IAuthorizeData.");
        authMetadata.ShouldAllBe(
            a => !string.IsNullOrWhiteSpace(a.Policy),
            $"{method} {rawPattern} must not have bare RequireAuthorization() (Policy == null).");
        authMetadata.ShouldContain(
            a => string.Equals(a.Policy, expectedPolicy, StringComparison.Ordinal),
            $"{method} {rawPattern} expected policy '{expectedPolicy}' but had [{string.Join(", ", authMetadata.Select(a => a.Policy))}].");
    }

    private static void AssertAnonymousWebhook(IReadOnlyList<RouteEndpoint> endpoints, string method, string rawPattern)
    {
        var endpoint = endpoints.Single(e =>
            string.Equals(e.RoutePattern.RawText, rawPattern, StringComparison.Ordinal) &&
            (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) ?? false));
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull($"{method} {rawPattern} must keep AllowAnonymous().");
    }

    private static void AssertAllProtectedRoutesHaveNamedPolicy(IReadOnlyList<RouteEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                continue;
            }

            var authData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            authData.ShouldNotBeEmpty($"Protected route '{endpoint.RoutePattern.RawText}' lacks authorization metadata.");
            authData.ShouldAllBe(
                a => !string.IsNullOrWhiteSpace(a.Policy),
                $"Protected route '{endpoint.RoutePattern.RawText}' still uses bare RequireAuthorization() (Policy == null).");
        }
    }

    [Fact]
    public void CallerAttributeAdminEndpoints_AttachExpectedNamedPolicies_AndNoBareRequireAuthorization()
    {
        var endpoints = MapAndGetEndpoints(b => b.MapCallerAttributeAdminEndpoints());
        endpoints.Count.ShouldBe(10);
        AssertAllProtectedRoutesHaveNamedPolicy(endpoints);

        const string prefix = "/api/v1/governance/caller-attributes";
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/", GatewayPolicies.CallerAttributeRead);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/service-sets", GatewayPolicies.CallerAttributeRead);
        AssertProtectedRoutePolicy(endpoints, "PUT", $"{prefix}/{{name}}", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "DELETE", $"{prefix}/{{name}}", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "PUT", $"{prefix}/service-sets", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/{{name}}/approve", GatewayPolicies.CallerAttributeApprove);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/{{name}}/reject", GatewayPolicies.CallerAttributeApprove);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/service-sets/approve", GatewayPolicies.CallerAttributeApprove);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/service-sets/reject", GatewayPolicies.CallerAttributeApprove);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/simulate", GatewayPolicies.CallerAttributeSimulate);
    }

    [Fact]
    public void VirtualFilterEndpoints_AttachExpectedNamedPolicies_AndNoBareRequireAuthorization()
    {
        var endpoints = MapAndGetEndpoints(b => b.MapVirtualFilterEndpoints());
        endpoints.Count.ShouldBe(14);
        AssertAllProtectedRoutesHaveNamedPolicy(endpoints);

        const string prefix = "/api/v1/governance";
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/virtual-filters", GatewayPolicies.VirtualFilterRead);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/config-sync/status", GatewayPolicies.VirtualFilterRead);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/virtual-filters/sync/status", GatewayPolicies.VirtualFilterRead);
        AssertProtectedRoutePolicy(endpoints, "PUT", $"{prefix}/virtual-filters/{{name}}", GatewayPolicies.VirtualFilterWrite);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/virtual-filters/{{name}}/approve", GatewayPolicies.VirtualFilterApprove);
        AssertProtectedRoutePolicy(endpoints, "DELETE", $"{prefix}/virtual-filters/{{name}}", GatewayPolicies.FilterAdmin);
        AssertProtectedRoutePolicy(endpoints, "PUT", $"{prefix}/access-profiles/{{name}}", GatewayPolicies.FilterAdmin);
        AssertProtectedRoutePolicy(endpoints, "DELETE", $"{prefix}/access-profiles/{{name}}", GatewayPolicies.FilterAdmin);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/access-profiles/{{name}}/approve", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/virtual-filters/sync/plan", GatewayPolicies.VirtualFilterSyncPlan);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/virtual-filters/sync/apply", GatewayPolicies.VirtualFilterSyncApply);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/effective-filters", GatewayPolicies.EffectiveFilterInspect);
        AssertAnonymousWebhook(endpoints, "POST", "/api/webhooks/config-sync");
        AssertAnonymousWebhook(endpoints, "POST", $"{prefix}/virtual-filters/sync/webhook");
    }

    [Fact]
    public void ProductEndpoints_AttachExpectedNamedPolicies_AndNoBareRequireAuthorization()
    {
        var endpoints = MapAndGetEndpoints(b => b.MapProductEndpoints());
        endpoints.Count.ShouldBe(13);
        AssertAllProtectedRoutesHaveNamedPolicy(endpoints);

        const string prefix = "/api/v1/products";
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/{{schema}}/{{name}}", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "PUT", $"{prefix}/{{schema}}/{{name}}", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "PUT", $"{prefix}/{{schema}}/{{name}}/owner-group", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/{{schema}}/{{name}}/retire", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/{{schema}}/{{name}}/settings", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/{{schema}}/{{name}}/columns/{{column}}/classification", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/discovery", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/{{schema}}/{{name}}/explain", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/{{schema}}/{{name}}/usage", GatewayPolicies.ProductAccess);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/sync/plan", GatewayPolicies.ProductSync);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/sync/apply", GatewayPolicies.ProductSync);
        AssertAnonymousWebhook(endpoints, "POST", $"{prefix}/sync/webhook/{{tenant}}");
    }

    [Fact]
    public void GovernanceEndpoints_AttachExpectedNamedPolicies_AndNoBareRequireAuthorization()
    {
        var endpoints = MapAndGetEndpoints(b => b.MapGovernanceEndpoints());
        endpoints.Count.ShouldBe(15);
        AssertAllProtectedRoutesHaveNamedPolicy(endpoints);

        AssertProtectedRoutePolicy(endpoints, "POST", "/api/governance/catalog/ingest-openapi", GatewayPolicies.OpenApiPublish);
        AssertProtectedRoutePolicy(endpoints, "POST", "/api/v1/consents/bulk", GatewayPolicies.ProfileAdmin);
        AssertProtectedRoutePolicy(endpoints, "DELETE", "/api/v1/profiles/{id}", GatewayPolicies.ProfileAdmin);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/v1/profiles", GatewayPolicies.ProfileRead);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/v1/profiles/{id}", GatewayPolicies.ProfileRead);
        AssertProtectedRoutePolicy(endpoints, "POST", "/api/v1/profiles/{id}/assignments/approve", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "POST", "/api/v1/governance/consents/apply", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "POST", "/api/governance/consents/apply", GatewayPolicies.GovernanceAdmin);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/v1/governance/audit", GatewayPolicies.AuditRead);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/v1/governance/grants", GatewayPolicies.AuditRead);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/governance/sunsetting/rules", GatewayPolicies.SunsettingRead);
        AssertProtectedRoutePolicy(endpoints, "POST", "/api/governance/sunsetting/rules", GatewayPolicies.ClusterAdmin);
        AssertProtectedRoutePolicy(endpoints, "POST", "/api/governance/sunsetting/evaluate", GatewayPolicies.SunsettingEvaluate);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/governance/eu-ai-act/article-10-certificate", GatewayPolicies.ComplianceCertificateRead);
        AssertProtectedRoutePolicy(endpoints, "GET", "/api/governance/gdpr/export-pdf", GatewayPolicies.AuthenticatedCaller);
    }

    [Fact]
    public void SqlEndpointRoutes_AttachExpectedNamedPolicies_AndNoBareRequireAuthorization()
    {
        var endpoints = MapAndGetEndpoints(b => b.MapSqlEndpoints());
        endpoints.Count.ShouldBe(5);
        AssertAllProtectedRoutesHaveNamedPolicy(endpoints);

        const string prefix = "/api/v1/queries";
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/", GatewayPolicies.AuthenticatedCaller);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/", GatewayPolicies.SqlEndpointRegister);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/openapi.json", GatewayPolicies.AuthenticatedCaller);
        AssertProtectedRoutePolicy(endpoints, "GET", $"{prefix}/{{name}}", GatewayPolicies.AuthenticatedCaller);
        AssertProtectedRoutePolicy(endpoints, "POST", $"{prefix}/{{name}}", GatewayPolicies.AuthenticatedCaller);
    }

    private static ServiceProvider BuildAuthorizationProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(
        string scheme = "Bearer",
        string? sid = "S-1-5-21-TEST",
        bool readOnly = false,
        params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        if (sid is not null)
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, sid));
        }

        claims.Add(new Claim(ClaimTypes.Name, "test-user"));
        var identity = new ClaimsIdentity(claims, scheme);
        if (readOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return new ClaimsPrincipal(identity);
    }

    [Theory]
    [InlineData(GatewayPolicies.CallerAttributeRead, new[] { "GovernanceAdmin", "SecurityAuditor", "FilterAdmin", "CallerAttributeApprover" }, "Consumer")]
    [InlineData(GatewayPolicies.CallerAttributeApprove, new[] { "GovernanceAdmin", "CallerAttributeApprover" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.CallerAttributeSimulate, new[] { "GovernanceAdmin", "FilterAdmin" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.VirtualFilterRead, new[] { "FilterAdmin", "FilterSync", "GovernanceAdmin", "SecurityAuditor" }, "Consumer")]
    [InlineData(GatewayPolicies.VirtualFilterWrite, new[] { "FilterAdmin", "GovernanceAdmin" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.VirtualFilterApprove, new[] { "GovernanceAdmin" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.FilterAdmin, new[] { "FilterAdmin" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.VirtualFilterSyncPlan, new[] { "FilterSync", "FilterAdmin" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.VirtualFilterSyncApply, new[] { "FilterSync" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.EffectiveFilterInspect, new[] { "FilterAdmin", "GovernanceAdmin", "SecurityAuditor" }, "Consumer")]
    [InlineData(GatewayPolicies.ProductSync, new[] { "DataProductSync" }, "ClusterAdmin")]
    [InlineData(GatewayPolicies.OpenApiPublish, new[] { "GovernanceAdmin", "SchemaPublisher", "TenantAdmin" }, "Consumer")]
    [InlineData(GatewayPolicies.ProfileAdmin, new[] { "TenantAdmin", "GovernanceAdmin" }, "SecurityAuditor")]
    [InlineData(GatewayPolicies.ProfileRead, new[] { "TenantAdmin", "GovernanceAdmin", "SecurityAuditor" }, "Consumer")]
    [InlineData(GatewayPolicies.AuditRead, new[] { "GovernanceAdmin", "SecurityAuditor" }, "Consumer")]
    [InlineData(GatewayPolicies.SunsettingRead, new[] { "GovernanceAdmin", "SchemaPublisher", "TenantAdmin" }, "SecurityAuditor")]
    [InlineData(GatewayPolicies.SunsettingEvaluate, new[] { "GovernanceAdmin", "SchemaPublisher", "Consumer", "TenantAdmin" }, "FilterSync")]
    [InlineData(GatewayPolicies.ComplianceCertificateRead, new[] { "GovernanceAdmin", "TenantAdmin", "SecurityAuditor" }, "Consumer")]
    public async Task RoleBasedPolicies_EvaluateAllowedAndDeniedRoles_WithAndWithoutHttpContext(
        string policyName,
        string[] allowedRoles,
        string deniedRole)
    {
        await using var provider = BuildAuthorizationProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        GatewayPolicies.TryGetPolicyRoles(policyName, out var registeredRoles).ShouldBeTrue();
        registeredRoles.ShouldBe(allowedRoles);

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        (await authz.AuthorizeAsync(anonymous, null, policyName)).Succeeded.ShouldBeFalse();

        foreach (var role in allowedRoles)
        {
            var user = Principal(roles: role);
            (await authz.AuthorizeAsync(user, null, policyName)).Succeeded.ShouldBeTrue($"{policyName} should allow {role} without HttpContext.");
            var httpContext = new DefaultHttpContext { User = user };
            (await authz.AuthorizeAsync(user, httpContext, policyName)).Succeeded.ShouldBeTrue($"{policyName} should allow {role} with HttpContext.");
        }

        var deniedUser = Principal(roles: deniedRole);
        (await authz.AuthorizeAsync(deniedUser, null, policyName)).Succeeded.ShouldBeFalse($"{policyName} should deny {deniedRole} without HttpContext.");
        (await authz.AuthorizeAsync(deniedUser, new DefaultHttpContext { User = deniedUser }, policyName)).Succeeded.ShouldBeFalse($"{policyName} should deny {deniedRole} with HttpContext.");

        // RV-02: authenticated user on HttpContext without SID claim must fail closed.
        var noSidUser = Principal(sid: null, roles: allowedRoles[0]);
        (await authz.AuthorizeAsync(noSidUser, new DefaultHttpContext { User = noSidUser }, policyName)).Succeeded.ShouldBeFalse();

        // RV-03: ForwardAuth ClusterAdmin must never satisfy ClusterAdmin-authorized policies.
        var forgedClusterAdmin = Principal(scheme: ClusterAdminPolicy.ForwardAuthScheme, roles: "ClusterAdmin");
        (await authz.AuthorizeAsync(forgedClusterAdmin, new DefaultHttpContext { User = forgedClusterAdmin }, policyName)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(forgedClusterAdmin, null, policyName)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task HasSecurityRole_ReadsPreResolvedSecurityPrincipalContext_FromHttpContext()
    {
        await using var provider = BuildAuthorizationProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        var claimsOnlyConsumer = Principal(roles: "Consumer");
        var httpContext = new DefaultHttpContext { User = claimsOnlyConsumer };
        httpContext.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ENRICHED"),
            TenantId = new TenantId("tenant-a"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(["CallerAttributeApprover"]),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        (await authz.AuthorizeAsync(claimsOnlyConsumer, httpContext, GatewayPolicies.CallerAttributeApprove)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(claimsOnlyConsumer, httpContext, GatewayPolicies.VirtualFilterWrite)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ProductAccess_BlocksSyncOnlyCallers_AndAllowsOwnersAdminsAndMixedAdminSync()
    {
        await using var provider = BuildAuthorizationProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        (await authz.AuthorizeAsync(anonymous, null, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeFalse();

        var ownerWithoutRoles = Principal();
        (await authz.AuthorizeAsync(ownerWithoutRoles, null, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(ownerWithoutRoles, new DefaultHttpContext { User = ownerWithoutRoles }, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeTrue();

        var syncOnly = Principal(roles: "DataProductSync");
        (await authz.AuthorizeAsync(syncOnly, null, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(syncOnly, new DefaultHttpContext { User = syncOnly }, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeFalse();

        var syncAndConsumer = Principal(roles: ["DataProductSync", "Consumer"]);
        (await authz.AuthorizeAsync(syncAndConsumer, new DefaultHttpContext { User = syncAndConsumer }, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeFalse();

        var syncAndProductAdmin = Principal(roles: ["DataProductSync", "DataProductAdmin"]);
        (await authz.AuthorizeAsync(syncAndProductAdmin, null, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(syncAndProductAdmin, new DefaultHttpContext { User = syncAndProductAdmin }, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeTrue();

        var syncAndClusterAdmin = Principal(roles: ["DataProductSync", "ClusterAdmin"]);
        (await authz.AuthorizeAsync(syncAndClusterAdmin, new DefaultHttpContext { User = syncAndClusterAdmin }, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeTrue();

        var syncAndForgedClusterAdmin = Principal(scheme: ClusterAdminPolicy.ForwardAuthScheme, roles: ["DataProductSync", "ClusterAdmin"]);
        (await authz.AuthorizeAsync(syncAndForgedClusterAdmin, new DefaultHttpContext { User = syncAndForgedClusterAdmin }, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task SqlEndpointRegister_RequiresCanonicalWritableClusterAdmin()
    {
        await using var provider = BuildAuthorizationProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        GatewayPolicies.ClusterAdminRoles.ShouldBeEmpty();
        GatewayPolicies.TryGetPolicyRoles(GatewayPolicies.SqlEndpointRegister, out _).ShouldBeFalse();

        var canonical = Principal(roles: "ClusterAdmin");
        (await authz.AuthorizeAsync(canonical, null, GatewayPolicies.SqlEndpointRegister)).Succeeded.ShouldBeTrue();

        var readOnlyAdmin = Principal(readOnly: true, roles: "ClusterAdmin");
        (await authz.AuthorizeAsync(readOnlyAdmin, null, GatewayPolicies.SqlEndpointRegister)).Succeeded.ShouldBeFalse();

        var forwardAuthAdmin = Principal(scheme: ClusterAdminPolicy.ForwardAuthScheme, roles: "ClusterAdmin");
        (await authz.AuthorizeAsync(forwardAuthAdmin, null, GatewayPolicies.SqlEndpointRegister)).Succeeded.ShouldBeFalse();

        var govAdmin = Principal(roles: "GovernanceAdmin");
        (await authz.AuthorizeAsync(govAdmin, null, GatewayPolicies.SqlEndpointRegister)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task AuthorizationPolicies_WithInvalidTenantHeader_FailClosedInsteadOfThrowing()
    {
        await using var provider = BuildAuthorizationProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        var clusterAdmin = Principal(roles: "ClusterAdmin");

        foreach (var invalidTenant in new[] { "_platform", "bad!tenant" })
        {
            var httpContext = new DefaultHttpContext { User = clusterAdmin };
            httpContext.Request.Headers[SecurityContextFactory.TenantHeaderName] = invalidTenant;

            (await authz.AuthorizeAsync(clusterAdmin, httpContext, GatewayPolicies.VirtualFilterWrite)).Succeeded.ShouldBeFalse();
            (await authz.AuthorizeAsync(clusterAdmin, httpContext, GatewayPolicies.ProductAccess)).Succeeded.ShouldBeFalse();
        }
    }

    private sealed class ServiceRecognizingProvider(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

        public bool IsService(Type serviceType) =>
            serviceType.IsInterface ||
            ConcreteEndpointServices.Contains(serviceType) ||
            inner.GetService(serviceType) != null;

        public bool IsKeyedService(Type serviceType, object? serviceKey) => false;
    }

    private sealed class RouteInspectorBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
