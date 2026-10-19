namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Api.Hosting;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// REQ-CDC-99 I1-C registrations: stream crypto over the key vault, the wake-up signal, the shared tail hub, retention, the I1
/// compatibility feed and the stream admin service. The persistence (<c>ICdcStreamStore</c>, <c>ICdcStreamKeyStore</c>) is registered with the
/// governance repositories and the producer/restarter with the CDC source integration.
/// </summary>
public static class CdcStreamServiceExtensions
{
    public static IServiceCollection AddAutherisCdcStream(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICdcKeyEncryptionKeyProvider, KeyVaultCdcKekProvider>();
        services.TryAddSingleton<CdcStreamKeyring>();
        services.TryAddSingleton<ICdcStreamCrypto, CdcStreamCrypto>();
        services.TryAddSingleton<ICdcStreamSignal, CdcStreamSignal>();
        services.TryAddSingleton<ICdcStreamTailHub, CdcStreamTailHub>();
        services.TryAddSingleton<CdcStreamCompatFeed>();
        services.TryAddSingleton<CdcStreamAdminService>();
        services.TryAddScoped<CdcStreamRetentionJob>();
        services.AddHostedService<CdcStreamRetentionService>();

        if (gatewayOptions.MssqlChangeTracking.Enabled)
        {
            services.AddHostedService<CdcStreamStartupCheck>();
        }

        return services;
    }
}
