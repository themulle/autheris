namespace Autheris.Api.Extensions.DependencyInjection;

using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Single registration-time read of the CDC webhook feature flag (the options object is not available while services are being registered).</summary>
internal static class CdcWebhooksFeature
{
    internal static bool IsEnabled(IServiceCollection services)
    {
        var configuration = services.FirstOrDefault(d => d.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
        return configuration?.GetSection(CdcWebhooksOptions.SectionName)
            .GetValue(nameof(CdcWebhooksOptions.Enabled), false) == true;
    }
}
