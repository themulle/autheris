namespace Autheris.Tests.Unit.Audit;

using Autheris.Api.Endpoints;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>The explain service writes its own fail-closed records, so the endpoints delegate the audit record to it.</summary>
public sealed class AuthorizationExplainEndpointAuditTests
{
    private sealed class PermissiveProvider(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

        public bool IsService(Type serviceType) => serviceType.IsInterface || inner.GetService(serviceType) != null;

        public bool IsKeyedService(Type serviceType, object? serviceKey) => false;
    }

    private sealed class TestRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    [Theory]
    [InlineData("/api/v1/authz/explain", AuditEventTypes.AuthzExplain)]
    [InlineData("/api/v1/authz/objects/{objectName}/grants", AuditEventTypes.AuthzGrantsList)]
    public void Explain_endpoints_delegate_their_audit_record_to_the_service(string route, string eventType)
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        var routes = new TestRouteBuilder(new PermissiveProvider(services.BuildServiceProvider()));
        routes.MapAuthorizationExplainEndpoints();

        var endpoint = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route);

        var policy = endpoint.Metadata.GetMetadata<AuditPolicy>().ShouldNotBeNull();
        policy.Level.ShouldBe(AuditLevel.Delegated);
        policy.EventType.ShouldBe(eventType);
    }
}
