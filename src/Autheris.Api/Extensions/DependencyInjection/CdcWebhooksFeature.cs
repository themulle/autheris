namespace Autheris.Api.Extensions.DependencyInjection;

using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>The CDC webhook feature flag as read from configuration while the services were being registered.</summary>
internal sealed record CdcWebhooksFeatureFlag(bool Enabled);

/// <summary>
/// Single registration-time read (G-2: an absent key means the default of <see cref="CdcWebhooksOptions.Enabled"/>, now <c>true</c>) of the CDC webhook feature flag (the options object is not available while services are being registered).
/// <see cref="Capture"/> stores the value once from the host configuration (<c>AddGatewayOptions</c>); a host builder registers
/// <c>IConfiguration</c> through a factory, so its instance cannot be read back from the service collection.
/// </summary>
internal static class CdcWebhooksFeature
{
    internal static void Capture(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new CdcWebhooksFeatureFlag(Read(configuration)));

    internal static bool IsEnabled(IServiceCollection services)
    {
        if (services.LastOrDefault(d => d.ServiceType == typeof(CdcWebhooksFeatureFlag))?.ImplementationInstance is CdcWebhooksFeatureFlag flag)
        {
            return flag.Enabled;
        }

        var configuration = services.FirstOrDefault(d => d.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
        // Absent configuration means the options-class default applies (G-2: enabled).
        return configuration is null ? new CdcWebhooksOptions().Enabled : Read(configuration);
    }

    private static bool Read(IConfiguration configuration) =>
        configuration.GetSection(CdcWebhooksOptions.SectionName).GetValue(nameof(CdcWebhooksOptions.Enabled), new CdcWebhooksOptions().Enabled);
}
