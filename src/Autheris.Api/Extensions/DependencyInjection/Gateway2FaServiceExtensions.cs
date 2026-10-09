namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Microsoft.Extensions.DependencyInjection;

public static class Gateway2FaServiceExtensions
{
    /// <summary>
    /// Registers RFC 6238 TOTP Two-Factor Authentication services and secret stores (ADR-05).
    /// </summary>
    public static IServiceCollection AddAutheris2Fa(this IServiceCollection services)
    {
        services.AddSingleton<ITotpSecretStore>(sp =>
        {
            var cluster = sp.GetService<IDistributedClusterStateProvider>();
            return new ClusterTotpSecretStore(cluster);
        });

        services.AddSingleton<ITotpVerificationService>(sp =>
        {
            var cluster = sp.GetService<IDistributedClusterStateProvider>();
            return new TotpVerificationService(cluster);
        });

        return services;
    }
}
