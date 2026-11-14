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
