namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.IO;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Mesh.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Rebac;
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
                logger);
            if (options.Casbin.Enabled && !string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
            {
                service.LoadPolicyFromFile(options.Casbin.PolicyPath, options.Casbin.WatchPolicyFile);
            }
            return service;
        });

        // ReBAC / OpenFGA Relationship-Based Access Control (F-SEC-04)
        // RR-L4-04: cluster-wide persistent tuple store whenever Redis/Garnet is configured; in-memory only for single-node.
        services.AddSingleton<IRebacStore>(sp =>
        {
            var multiplexer = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            var opts = sp.GetRequiredService<IOptions<GatewayOptions>>();
            return multiplexer != null
                ? new RedisRebacStore(
                    multiplexer,
                    opts,
                    sp.GetRequiredService<ILogger<RedisRebacStore>>())
                : new InMemoryRebacStore(
                    sp.GetRequiredService<ILogger<InMemoryRebacStore>>());
        });
        services.AddSingleton<IRebacEvaluator, ZanzibarRebacEvaluator>();
        services.AddScoped<IRebacBatchDataLoader, RebacBatchDataLoader>();

        // AR-06 / SEC-Invariant 4: Async ReBAC seeding without sync-over-async blocking
        services.AddHostedService<RebacSeedHostedService>();

        // Security, Context & Audit
        services.AddScoped<IClientIpResolver, HttpContextClientIpResolver>();
        services.AddScoped<AuditContext>();
        services.AddSingleton<IAuthFailureAggregator, AuthFailureAggregator>();
        services.AddSingleton<ITableReadConcurrencyGate, TableReadConcurrencyGate>();
        services.AddSingleton<IDbSessionContextInitializer, DbSessionContextInitializer>();

        // F-ARCH-11: Envoy External Authorization & Istio Service Mesh Adapter
        services.AddSingleton<IEnvoyExtAuthzService, EnvoyExtAuthzService>();

        return services;
    }
}
