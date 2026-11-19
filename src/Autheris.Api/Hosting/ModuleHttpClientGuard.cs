namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Autheris.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// Fail-closed egress rule for modules: every <see cref="IHttpClientFactory"/> client a module configures must carry the
/// <see cref="SsrfProtectionHandler"/> in its handler chain and must not follow redirects. The check replays the client
/// configuration the module added to the service collection against a probe builder; a configuration that cannot be replayed
/// without services does not count as protected.
/// </summary>
internal static class ModuleHttpClientGuard
{
    internal static List<ServiceDescriptor> CaptureClientConfiguration(IServiceCollection services) =>
        services.Where(IsClientConfiguration).ToList();

    internal static void VerifyModuleClientsUseSsrfHandler(
        string moduleId,
        IServiceCollection services,
        List<ServiceDescriptor> beforeSnapshot)
    {
        var added = services.Where(d => IsClientConfiguration(d) && !beforeSnapshot.Contains(d)).ToList();
        var names = added
            .Select(d => (d.ImplementationInstance as ConfigureNamedOptions<HttpClientFactoryOptions>)?.Name)
            .Where(n => n is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A client configuration the check cannot read (an options configurator that is not a plain named action) is refused.
        if (added.Any(d => d.ImplementationInstance is not ConfigureNamedOptions<HttpClientFactoryOptions>))
        {
            throw new ModuleLoadException(
                moduleId,
                $"Module '{moduleId}' registered an HttpClient configuration the host cannot verify; use AddSecureOutboundHandlers.");
        }

        foreach (var name in names)
        {
            if (!ChainHasSsrfHandler(services, name!))
            {
                throw new ModuleLoadException(
                    moduleId,
                    $"Module '{moduleId}' registered HttpClient '{name}' without the SSRF protection handler (use AddSecureOutboundHandlers).");
            }
        }
    }

    /// <summary>True when the configured chain of the named client has the SSRF handler and a primary handler that does not follow redirects.</summary>
    internal static bool ChainHasSsrfHandler(IServiceCollection services, string clientName)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new HttpClientFactoryOptions();
        foreach (var descriptor in services.Where(IsClientConfiguration))
        {
            if (descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> named &&
                string.Equals(named.Name, clientName, StringComparison.Ordinal))
            {
                named.Action?.Invoke(options);
            }
        }

        using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var probe = new ProbeHandlerBuilder(emptyProvider) { Name = clientName };
        try
        {
            foreach (var action in options.HttpMessageHandlerBuilderActions)
            {
                action(probe);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        var ssrf = probe.AdditionalHandlers.Any(h => h is SsrfProtectionHandler);
        var redirects = probe.PrimaryHandler is not SocketsHttpHandler { AllowAutoRedirect: true } &&
                        probe.PrimaryHandler is not HttpClientHandler { AllowAutoRedirect: true };
        return ssrf && redirects;
    }

    private static bool IsClientConfiguration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType.IsConstructedGenericType &&
        descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IConfigureOptions<>) &&
        descriptor.ServiceType.GetGenericArguments()[0] == typeof(HttpClientFactoryOptions);

    private sealed class ProbeHandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services { get; } = services;

        public override HttpMessageHandler Build() => throw new NotSupportedException("The probe builder never builds a handler.");
    }
}
