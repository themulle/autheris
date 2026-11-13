namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.IO;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mesh.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class GatewaySecurityServiceExtensions
{
    public static IServiceCollection AddAutherisSecurity(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        services.AddOptions<CasbinDecisionCacheOptions>()
            .BindConfiguration(CasbinDecisionCacheOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CasbinDecisionCacheOptions>, CasbinDecisionCacheOptionsValidator>();

        // Casbin ABAC Engine
        services.AddSingleton<IPolicyEnforcementService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
            var rlsGen = sp.GetService<IRlsFilterGenerator>();
            var logger = sp.GetService<ILogger<CasbinEnforcementService>>();
            if (!options.Casbin.Enabled)
            {
                logger?.LogWarning("Casbin ABAC engine is DISABLED (Gateway:Casbin:Enabled = false). Access control via Casbin policies is inactive.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(options.Casbin.ModelPath))
                {
                    var fullModelPath = Path.GetFullPath(options.Casbin.ModelPath);
                    if (!File.Exists(fullModelPath))
                    {
                        throw new FileNotFoundException($"Gateway:Casbin is enabled, but model file '{fullModelPath}' was not found. Failing closed.");
                    }
                }
                if (string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
                {
                    throw new InvalidOperationException("Gateway:Casbin is enabled, but PolicyPath is not configured. Failing closed.");
                }

                var fullPolicyPath = Path.GetFullPath(options.Casbin.PolicyPath);
                if (!File.Exists(fullPolicyPath))
                {
                    throw new FileNotFoundException($"Gateway:Casbin is enabled, but policy file '{fullPolicyPath}' was not found. Failing closed.");
                }
            }

            var service = new CasbinEnforcementService(
                string.IsNullOrWhiteSpace(options.Casbin.ModelPath) ? null : options.Casbin.ModelPath,
                rlsGen,
                logger,
                // WP-X0l: lets the Casbin decisions carry their row filters as typed policies.
                sp.GetService<Autheris.Application.Policy.RowPolicyFactory>(),
                sp.GetRequiredService<IOptions<CasbinDecisionCacheOptions>>().Value,
                TimeProvider.System,
                sp.GetService<IDistributedClusterStateProvider>(),
                sp.GetService<IClusterStateTopology>());
            if (options.Casbin.Enabled && !string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
            {
                service.LoadPolicyFromFile(options.Casbin.PolicyPath, options.Casbin.WatchPolicyFile);
            }
            return service;
        });

        // Security, Context & Audit
        services.AddHttpContextAccessor();
        services.AddScoped<IClientIpResolver, HttpContextClientIpResolver>();
        services.AddSingleton<Autheris.Application.Common.Interfaces.ICallerContextProvider, Autheris.Api.Security.HttpCallerContextProvider>();
        services.AddSingleton<Autheris.Application.Common.Interfaces.IDelegationContextProvider, Autheris.Api.Security.HttpDelegationContextProvider>();
        services.AddScoped<AuditContext>();
        services.AddSingleton<IAuthFailureAggregator, AuthFailureAggregator>();
        services.AddSingleton<ITableReadConcurrencyGate, TableReadConcurrencyGate>();
        services.AddSingleton<IDbSessionContextInitializer, DbSessionContextInitializer>();

        // F-ARCH-11: Envoy External Authorization & Istio Service Mesh Adapter
        services.AddScoped<IEnvoyExtAuthzService, EnvoyExtAuthzService>();

        // RFC 6238 TOTP 2FA Engine (ADR-05)
        services.AddAutheris2Fa();

        return services;
    }
}
