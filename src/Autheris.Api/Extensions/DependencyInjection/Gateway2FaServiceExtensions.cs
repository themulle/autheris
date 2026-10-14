namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Api.Security;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
            var totpOptions = sp.GetService<IOptions<GatewayOptions>>()?.Value.Totp;

            // SR-L-05: opt-in encryption of TOTP seeds at rest (needs a persisted, shared Data Protection key ring)
            ITotpSecretProtector? protector = null;
            if (totpOptions?.EncryptSecretsAtRest == true)
            {
                var provider = sp.GetService<IDataProtectionProvider>()
                    ?? throw new InvalidOperationException("Totp.EncryptSecretsAtRest is enabled but no IDataProtectionProvider is registered.");
                protector = new DataProtectionTotpSecretProtector(provider);
            }

            return new ClusterTotpSecretStore(cluster, protector);
        });

        services.AddSingleton<ITotpVerificationService>(sp =>
        {
            var cluster = sp.GetService<IDistributedClusterStateProvider>();
            var totpOptions = sp.GetService<IOptions<GatewayOptions>>()?.Value.Totp;
            return new TotpVerificationService(cluster, totpOptions);
        });

        services.AddSingleton<ITotpEnrollmentService>(sp => new TotpEnrollmentService(
            sp.GetRequiredService<ITotpVerificationService>(),
            sp.GetRequiredService<ITotpSecretStore>(),
            sp.GetService<IDistributedClusterStateProvider>(),
            sp.GetService<IOptions<GatewayOptions>>()?.Value.Totp));

        return services;
    }
}
