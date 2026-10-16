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
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            .AddHttpMessageHandler(sp => SsrfProtectionHandler.Create(sp, "DeclarativeHttp"))
            // F-GOV-14 (SEC-17): an upstream body above the per-table staging cap aborts while it is read.
            // CR-09: configured by DataSources:Http:MaxResponseBytes (global to every user of this client).
            .AddHttpMessageHandler(sp => new ResponseSizeLimitingHandler(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.DataSources?.Http?.MaxResponseBytes is > 0 and var cap
                    ? cap
                    : new HttpDataSourceOptions().MaxResponseBytes));
#pragma warning restore CA5359

        services.AddSingleton<IPluginManager, PluginManager>();
        // WP-ARC-B-02: SQL table reads run through the governed pipeline (planner + IGovernedSqlExecutionService). Scoped, because the
        // execution service is; it is resolved lazily to keep the FederatedStagingService -> IEnumerable<IDataSourceExecutor> ->
        // IGovernedSqlExecutionService -> IFederatedQueryExecutionService cycle out of the constructor graph.
        services.TryAddSingleton<Autheris.Application.Data.Interfaces.IGovernedTableReadPlanner, Autheris.Application.Data.Services.GovernedTableReadPlanner>();
        services.AddScoped<SqlTableReadExecutor>(sp => new SqlTableReadExecutor(
            sp.GetRequiredService<Autheris.Application.Data.Interfaces.IGovernedTableReadPlanner>(),
            () => sp.GetRequiredService<IGovernedSqlExecutionService>(),
            sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()));
        services.AddScoped<IDataSourceExecutor>(sp => sp.GetRequiredService<SqlTableReadExecutor>());
        services.AddSingleton<IDataSourceExecutor, DeclarativeHttpDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, PluginHttpDataSourceExecutor>();
        services.AddSingleton<ISqlEngine, FastSqlEngine>();
        services.AddSingleton<ISqlSecurityValidator, DefaultSqlSecurityValidator>();
        // L-A1: one key resolution for the process (singleton), shared by the governed and the federated executors.
        services.AddSingleton<CompiledSqlAuditDigester>();
        services.AddScoped<GovernedSqlRewriteDependencies>();
        services.AddScoped<GovernedSqlExecutionDependencies>();
        // R-4: the federated executor is resolved on first cross-source use (breaks the construction cycle without a service locator).
        services.AddScoped(sp => new Lazy<IFederatedQueryExecutionService>(() => sp.GetRequiredService<IFederatedQueryExecutionService>()));
        services.AddScoped<IGovernedSqlExecutionService, GovernedSqlExecutionService>();
        if (gatewayOptions.WebSql.Enabled)
        {
            // L-2: an unusable audit digest key stops the start outside Development and Test.
            services.AddHostedService<AuditKeyStartupCheck>();
            // SEC-ADG-26: the start lists the tables that the governed pipeline denies (ids only).
            services.AddHostedService<DeniedTablesStartupReporter>();
        }
        services.AddSingleton<IWebSqlStatementManager, WebSqlStatementManager>();
        services.AddSingleton<ISqlEndpointRegistry, InMemorySqlEndpointRegistry>();
        services.AddSingleton<SqlEndpointLoader>();
        services.AddScoped<ISqlEndpointExecutionService, SqlEndpointExecutionService>();

        // WebSQL Federation & Cross-Source Joins (Plan 7)
        services.AddSingleton<CrossSourceQueryRouter>();
        services.AddSingleton<ICrossSourceQueryRouter>(sp => sp.GetRequiredService<CrossSourceQueryRouter>());
        services.AddSingleton<CrossSourcePlanner>();
        services.AddSingleton<ICrossSourcePlanner>(sp => sp.GetRequiredService<CrossSourcePlanner>());
        services.AddScoped<Autheris.Application.Olap.IFederatedStagingService, Autheris.Application.Olap.FederatedStagingService>();
        services.AddScoped<Autheris.Application.Olap.IOlapStagingService, Autheris.Application.Olap.OlapStagingService>();
        services.AddSingleton<Autheris.Application.Olap.IOlapRequestLimiter, Autheris.Application.Olap.OlapRequestLimiter>();
        services.AddSingleton<Autheris.Application.Federation.IFederationGate, Autheris.Application.Federation.FederationGate>();
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
