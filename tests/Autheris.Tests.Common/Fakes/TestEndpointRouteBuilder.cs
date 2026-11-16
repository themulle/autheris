namespace Autheris.Tests.Common.Fakes;

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Minimal in-memory <see cref="IEndpointRouteBuilder"/> for unit tests inspecting route registration and endpoint metadata without creating a host.
/// </summary>
public sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder, IAsyncDisposable, IDisposable
{
    private readonly ServiceProvider _ownedProvider;

    public TestEndpointRouteBuilder(Action<IServiceCollection>? configureServices = null, bool permissiveServices = false)
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddLogging();
        services.AddAuthorization();
        configureServices?.Invoke(services);
        _ownedProvider = services.BuildServiceProvider();
        ServiceProvider = permissiveServices ? new PermissiveProvider(_ownedProvider) : _ownedProvider;
    }

    public IServiceProvider ServiceProvider { get; }

    public ICollection<EndpointDataSource> DataSources { get; } = [];

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

    public void Dispose() => _ownedProvider.Dispose();

    public ValueTask DisposeAsync() => _ownedProvider.DisposeAsync();

    private sealed class PermissiveProvider(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

        public bool IsService(Type serviceType) => serviceType.IsInterface || inner.GetService(serviceType) != null;
    }
}
