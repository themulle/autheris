namespace Autheris.Tests.Unit.Catalog;

using Autheris.Api.Endpoints;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>SEC-36-SP2-02: the probe service writes its own required records, so the endpoint delegates the record to the service.</summary>
[Trait("Category", "Security")]
public sealed class DatasourceTestEndpointAuditTests
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

    [Fact]
    public void Probe_endpoint_delegates_its_audit_record_to_the_service()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        var routes = new TestRouteBuilder(new PermissiveProvider(services.BuildServiceProvider()));
        routes.MapCatalogEndpoints();

        var endpoint = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v1/catalog/datasources/{id}/test");

        var policy = endpoint.Metadata.GetMetadata<AuditPolicy>().ShouldNotBeNull();
        policy.Level.ShouldBe(AuditLevel.Delegated);
        policy.EventType.ShouldBe(AuditEventTypes.DatasourceTested);
    }
}
