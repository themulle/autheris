namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Net.Http;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Plugins;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Procedures.Tools;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Plugins;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using TrinoSqlEngine;

public static class GatewaySqlEngineServiceExtensions
{
    public static IServiceCollection AddAutherisSqlEngine(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        // HTTP & Plugin Data Sources
#pragma warning disable CA5359 // Intentionally allowed via danger_allow_untrusted_certificates for Getting Started / Dev
        if (gatewayOptions.AreUntrustedCertificatesAllowed)
        {
            services.ConfigureHttpClientDefaults(builder =>
            {
                builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
            });
        }

        services.AddHttpClient(DeclarativeHttpDataSourceExecutor.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => SecureOutboundHttp.CreatePrimaryHandler(sp, "DeclarativeHttp"))
            // SEC I-1: same URL/address decision as every other integration client (also used by plugins).
            .AddHttpMessageHandler(sp => SsrfProtectionHandler.Create(sp, "DeclarativeHttp"));
#pragma warning restore CA5359

        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<IDataSourceExecutor, SqlDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, DeclarativeHttpDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, PluginHttpDataSourceExecutor>();
        services.AddSingleton<ISqlEngine, FastSqlEngine>();
        services.AddSingleton<ISqlSecurityValidator, DefaultSqlSecurityValidator>();
        services.AddSingleton<Autheris.Application.Sql.ICompiledSqlQueryPlanCache>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            var planCacheOptions = options.WebSql.PlanCache;
            if (planCacheOptions.MaxEntries <= 0)
            {
                return Autheris.Application.Sql.NullCompiledSqlQueryPlanCache.Instance;
            }

            var ttl = planCacheOptions.TtlSeconds > 0
                ? TimeSpan.FromSeconds(planCacheOptions.TtlSeconds)
                : TimeSpan.FromMinutes(10);

            return new Autheris.Application.Sql.CompiledSqlQueryPlanCache(planCacheOptions.MaxEntries, ttl);
        });
        services.AddScoped<IGovernedSqlExecutionService, GovernedSqlExecutionService>();
        services.AddSingleton<IWebSqlStatementManager, WebSqlStatementManager>();
        services.AddSingleton<ISqlEndpointRegistry, InMemorySqlEndpointRegistry>();
        services.AddScoped<ISqlEndpointExecutionService, SqlEndpointExecutionService>();

        // WebSQL Federation & Cross-Source Joins (Plan 7)
        services.AddSingleton<CrossSourceQueryRouter>();
        services.AddSingleton<ICrossSourceQueryRouter>(sp => sp.GetRequiredService<CrossSourceQueryRouter>());
        services.AddSingleton<CrossSourcePlanner>();
        services.AddSingleton<ICrossSourcePlanner>(sp => sp.GetRequiredService<CrossSourcePlanner>());
        services.AddScoped<Autheris.Application.Olap.IFederatedStagingService, Autheris.Application.Olap.FederatedStagingService>();
        services.AddScoped<IFederatedQueryExecutionService, FederatedDuckDbExecutionService>();

        // F-SQL-02: governed stored procedure endpoints (SQL Server, read-only in phase 1)
        services.AddSingleton<IProcedureRegistry, InMemoryProcedureRegistry>();
        services.AddSingleton<ProcedureDefinitionLoader>();
        services.AddSingleton<ProcedureConnectionProvider>();
        services.AddScoped<StoredProcedureCatalogValidator>();
        services.AddSingleton<IProcedureInvoker, MssqlProcedureInvoker>();
        services.AddSingleton<IProcedureRowScopeResolver, SqlProcedureRowScopeResolver>();
        services.AddScoped<IProcedureExecutionService, GovernedProcedureExecutionService>();
        services.AddSingleton<IProcedureYamlGenerator, ProcedureYamlGenerator>();
        if (gatewayOptions.SqlEndpoints.Procedures.Enabled)
        {
            services.AddHostedService<ProcedureRegistrationService>();
        }

        return services;
    }
}
