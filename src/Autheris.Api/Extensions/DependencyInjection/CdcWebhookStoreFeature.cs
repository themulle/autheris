namespace Autheris.Api.Extensions.DependencyInjection;

using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>The CDC webhook feature flag as read from host configuration while the services were being registered.</summary>
internal sealed record CdcWebhookStoreFlag(bool Enabled);

/// <summary>
/// Single registration-time read of <c>Gateway:Events:CdcWebhooks:Enabled</c> (an absent key means enabled). The host only needs
/// it to decide whether the durable subscription view of the governance repository is registered; the options, the endpoints
/// and the delivery service belong to the events module. <see cref="Capture"/> stores the value once from the host
/// configuration (<c>AddGatewayOptions</c>), because a host builder registers <c>IConfiguration</c> through a factory whose
/// instance cannot be read back from the service collection.
/// </summary>
internal static class CdcWebhookStoreFeature
{
    private const string EnabledKey = GatewaySections.Events + ":CdcWebhooks:Enabled";

    internal static void Capture(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new CdcWebhookStoreFlag(Read(configuration)));

    internal static bool IsEnabled(IServiceCollection services)
    {
        if (services.LastOrDefault(d => d.ServiceType == typeof(CdcWebhookStoreFlag))?.ImplementationInstance is CdcWebhookStoreFlag flag)
        {
            return flag.Enabled;
        }

        var configuration = services.FirstOrDefault(d => d.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
        // Absent configuration means the default applies (enabled).
        return configuration is null || Read(configuration);
    }

    private static bool Read(IConfiguration configuration) => configuration.GetValue(EnabledKey, true);
}
