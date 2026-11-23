namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using Autheris.Api.Hosting;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Observability.IncrementalDelivery;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Common.Serialization;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Tree;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Extensions;
using Autheris.Infrastructure.Streaming.Producers;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Connectors;
using Autheris.Domain.Diagnostics;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class GatewayExecutionServiceExtensions
{
    public static IServiceCollection AddAutherisExecution(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        // Standardisiertes Connector-SPI (F-ARCH-10 nach Trino-Muster)
        services.AddSingleton<IAutherisConnectorRegistry>(sp =>
        {
            var registry = new InMemoryConnectorRegistry();
            var metaRepo = sp.GetService<ITableMetadataRepository>();
            var opts = sp.GetService<IOptions<GatewayOptions>>();
            var maskingProvider = sp.GetService<IColumnMaskingProvider>();

            // 02 / ARC-I-13: the default SQL connector reads through the governed pipeline and requires the masking provider.
            if (metaRepo != null && maskingProvider != null)
            {
                var defaultSqlConnector = new SqlConnector(
                    connectorId: "default-sql",
                    metadataRepository: metaRepo,
                    maskingProvider: maskingProvider,
                    scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
                    options: opts);
                registry.RegisterConnector("default-sql", defaultSqlConnector);
                registry.RegisterConnector("sql", defaultSqlConnector);
            }
            return registry;
        });

        services.AddScoped<ICrossDomainAccessResolver, DefaultCrossDomainAccessResolver>();

        services.AddScoped<GatewayExecutionService>(sp => new GatewayExecutionService(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetRequiredService<IAuditLogRepository>(),
            sp.GetRequiredService<IConsentResolutionService>(),
            sp.GetRequiredService<IConsentCacheService>(),
            sp.GetRequiredService<IColumnMaskingProvider>(),
            sp.GetService<IOptions<GatewayOptions>>(),
            sp.GetService<ITrafficDrainController>(),
            sp.GetServices<IDataSourceExecutor>(),
            sp.GetService<IClientIpResolver>(),
            sp.GetService<IAutherisConnectorRegistry>(),
            sp.GetService<ITableReadConcurrencyGate>(),
            sp.GetRequiredService<IMandatoryRowFilterResolver>(),
            typedPolicies: sp.GetService<Autheris.Application.Policy.TypedPolicySources>(),
            auditWriter: sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
            usageRecorder: sp.GetService<Autheris.Application.Usage.IUsageRecorder>(),
            transportGate: sp.GetService<Autheris.Application.DataSources.IDataSourceTransportGate>(),
            accessDecisionPoint: sp.GetRequiredService<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>()));
        services.AddScoped<IGatewayExecutionService>(sp => sp.GetRequiredService<GatewayExecutionService>());
        services.AddScoped<ITableAccessResolver>(sp => sp.GetRequiredService<GatewayExecutionService>());
        // SQLA-06 follow-up (SQLA06F-C8): the secured-source builder is registered wherever the tree service is; without it a subquery policy is denied.
        services.AddSingleton<TreeSecuredSourceBuilder>(sp => new TreeSecuredSourceBuilder(
            sp.GetService<Autheris.Application.Sql.Policy.GovernancePolicyFactory>(),
            sp.GetService<TrinoSqlEngine.ISqlEngine>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TreeSecuredSourceBuilder>>()));
        services.AddScoped<IGovernedTreeQueryService, GovernedTreeQueryService>();
        services.AddScoped<Autheris.Application.Policy.WriteGrants.IWriteGrantEvaluator>(sp =>
            new Autheris.Application.Policy.WriteGrants.WriteGrantEvaluator(
                sp.GetRequiredService<IConsentRepository>(),
                sp.GetService<Autheris.Application.Policy.WriteGrants.IRowPolicyDependencyIndex>(),
                sp.GetService<TimeProvider>()));
        services.AddScoped<Autheris.Application.Policy.Pdp.IAccessDecisionPoint, Autheris.Application.Policy.Pdp.AccessDecisionPoint>();

        // Hierarchical Parquet Egress (F-DATA-01)
        services.AddSingleton<IParquetExportService, ParquetExportService>();

        // Native Apache Arrow IPC Egress (F-DATA-04 & F-DATA-04-B)
        services.AddSingleton<IArrowExportService, ArrowExportService>();

        // Embedded In-Memory OLAP via DuckDB.NET (F-DATA-03)
        services.AddSingleton<IDuckDbOlapEngine>(sp =>
            new DuckDbOlapEngine(
                sp.GetRequiredService<IOptions<GatewayOptions>>(),
                sp.GetService<ILogger<DuckDbOlapEngine>>()));

        // time zone conversion state (registry self-test and DuckDB ICU probe), evaluated once at startup; fail closed until then.
        services.AddSingleton<Autheris.Application.Sql.TimeZones.TimeZoneConversionState>();
        services.AddSingleton<Autheris.Application.Sql.TimeZones.ITimeZoneConversionState>(sp => sp.GetRequiredService<Autheris.Application.Sql.TimeZones.TimeZoneConversionState>());
        services.AddHostedService<Autheris.Application.Sql.TimeZones.TimeZoneConversionStartupCheck>();

        // HA & Traffic Drain
        services.AddSingleton<ITrafficDrainController, TrafficDrainController>();
        services.AddHostedService<TrafficDrainHostedService>();

        // Connectors to foreign systems (Autheris.Extensions)
        services.AddGatewayExtensions(gatewayOptions);
        services.AddCdcSourceIntegration(gatewayOptions);

        // Realtime Event Subscriptions & In-Stream RLS (P5, F-CDC-03)
        services.AddAutherisStreaming();
        services.AddSingleton<ICdcEventIngestionService, CdcEventIngestionService>();
        services.AddScoped<IStreamRlsPolicyEnforcer, StreamRlsPolicyEnforcer>();
        services.AddSingleton<PostgreSqlLogicalReplicationService>();
        services.AddSingleton<IPostgreSqlCdcService>(sp => sp.GetRequiredService<PostgreSqlLogicalReplicationService>());
        if (gatewayOptions.PostgreSqlCdc.Enabled)
        {
            services.AddHostedService(sp => sp.GetRequiredService<PostgreSqlLogicalReplicationService>());
        }

        // F-EVT-01: CloudEvents v1.0 outbound webhook subscriptions. The secret protector is a host service: it is always registered
        // and fails closed (WebhookSecretUnavailableException, never plaintext) when the key is not provisioned. The subscription
        // endpoints, the options and the delivery service belong to the events module; this block keeps only the host ports.
        services.AddSingleton<Autheris.Application.Webhooks.IWebhookSecretProtector, Autheris.Infrastructure.Security.KeyVaultWebhookSecretProtector>();
        if (CdcWebhookStoreFeature.IsEnabled(services))
        {
            // The durable subscription store is registered with the governance repositories (AddAutherisGovernance).
            services.AddSingleton<ICloudEventTransformer, CloudEventTransformer>();
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        }

        // Incremental Delivery (@defer & @stream) (F-PERF-12)
        services.AddSingleton<IIncrementalDeliveryFormatter, IncrementalDeliveryFormatter>();
        services.AddSingleton<IIncrementalDeliveryManager, IncrementalDeliveryManager>();

        return services;
    }
}
