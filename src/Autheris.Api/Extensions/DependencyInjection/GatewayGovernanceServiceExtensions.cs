namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.Webhooks;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;

public static class GatewayGovernanceServiceExtensions
{
    /// <summary>
    /// Registers the hosted service that migrates and initializes the governance store (WP-ARC-B-12, decision G-8). Must be called
    /// before any other hosted service is registered so <c>StartAsync</c> blocks the rest of the host until the schema is ready.
    /// </summary>
    public static IServiceCollection AddAutherisGovernanceMigrations(this IServiceCollection services)
    {
        services.AddHostedService<GovernanceMigrationHostedService>();
        return services;
    }

    public static IServiceCollection AddAutherisGovernance(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        services.AddSingleton<Autheris.Application.Audit.IAuditWriter, Autheris.Application.Audit.AuditWriter>();

        if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.PostgreSql))
        {
            services.AddSingleton<PostgreSqlGovernanceRepository>();
            services.AddSingleton<IGovernanceStoreInitializer>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IGovernanceMigrationRunner, PostgreSqlGovernanceMigrationRunner>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IWebhookOutbox>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository, InMemoryAccessProfileRepository>();
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ICdcWatermarkRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
        }
        else if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.SqlServer))
        {
            services.AddSingleton<SqlServerGovernanceRepository>();
            services.AddSingleton<IGovernanceStoreInitializer>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IGovernanceMigrationRunner, SqlServerGovernanceMigrationRunner>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IWebhookOutbox>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository, InMemoryAccessProfileRepository>();
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ICdcWatermarkRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
        }
        else
        {
            services.AddSingleton<SqliteGovernanceRepository>();
            services.AddSingleton<IGovernanceStoreInitializer>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IGovernanceMigrationRunner, SqliteGovernanceMigrationRunner>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IWebhookOutbox>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ICdcWatermarkRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        }

        // WP-ARC-A-11: durable, fenced CDC watermarks live in the governance database (the extension registers no store).
        services.AddSingleton<IMssqlWatermarkStore, GovernanceDbMssqlWatermarkStore>();

        if (gatewayOptions.TenantIsolation.Registry.Enabled)
        {
            // CR-ADG-31: the governance repository is the persistent registry; the gate adds the positive cache and the per-issuer budget.
            var registry = gatewayOptions.TenantIsolation.Registry;
            services.AddSingleton<ITenantRegistry>(sp => new TenantRegistryGate(
                (ITenantRegistry)sp.GetRequiredService<IGovernanceRepository>(),
                new TenantRegistryGateOptions(
                    registry.MaxNewTenantsPerIssuerPerWindow,
                    TimeSpan.FromSeconds(registry.WindowSeconds),
                    TimeSpan.FromSeconds(registry.CacheTtlSeconds),
                    registry.MaxCacheEntries,
                    registry.MaxTrackedIssuers)));
            services.AddHostedService<TenantRegistryPreseedService>();
        }

        services.AddSingleton<IAccessProfileCache, AccessProfileCache>();
        services.AddSingleton<IDbtProposalRepository, InMemoryDbtProposalRepository>();
        services.AddSingleton<IDbtHealthCircuitBreaker, DbtHealthCircuitBreaker>();

        services.AddSingleton<VirtualFilterAdministrationService>();
        services.AddSingleton<IVirtualFilterSnapshotProvider, VirtualFilterSnapshotProvider>();
        services.AddSingleton<IVirtualFilterPredicateBuilder, StructuredFilterSqlBuilder>();
        services.AddSingleton<MandatoryRowFilterResolver>();
        services.AddSingleton<IMandatoryRowFilterResolver>(sp => sp.GetRequiredService<MandatoryRowFilterResolver>());
        // F-GOV-14 (M4, SEC-10): authorized key sets from the SQL key source, in a dedicated size-limited cache.
        services.AddSingleton<Autheris.Application.VirtualFilters.Federation.VirtualFilterKeySetCache>();
        services.AddSingleton<Autheris.Application.VirtualFilters.Federation.IVirtualFilterKeySetProvider, Autheris.Application.VirtualFilters.Federation.SqlVirtualFilterKeySetProvider>();
        // F-GOV-14 (WI-6): without this registration a WebSQL cross-source query over a protected HTTP table is denied.
        services.AddSingleton<Autheris.Application.VirtualFilters.Federation.IVirtualFilterFederationPlanner, Autheris.Application.VirtualFilters.Federation.VirtualFilterFederationPlanner>();
        services.AddSingleton<IConsentCacheService, ConsentCacheService>();

        // WP-X0e: typed row policy. The renderer and the parser are stateless (the parser caches shapes), so one instance serves all.
        services.AddSingleton<Autheris.Application.Sql.Policy.IPolicyPredicateRenderer, Autheris.Application.Sql.Policy.PolicyPredicateRenderer>();
        services.AddSingleton<TrinoSqlEngine.Governance.IPolicyExpressionParser, TrinoSqlEngine.Governance.PolicyExpressionParser>();
        services.AddSingleton<Autheris.Application.Policy.RowPolicyFactory>();
        services.AddSingleton<Autheris.Application.Sql.Masking.SqlDataMaskingProvider>();
        services.AddSingleton<Autheris.Application.Policy.ColumnMaskPolicyFactory>();
        services.AddSingleton<Autheris.Application.Policy.TypedPolicySources>();
        // WP-X0l: assembles the typed GovernancePolicy of a statement (no caller until X1). The catalog provider is registered with the storage.
        services.AddSingleton<Autheris.Application.Sql.Policy.GovernancePolicyFactory>();
        services.AddSingleton<IRlsFilterGenerator, RlsFilterGenerator>();
        services.AddSingleton<IRowFilterSqlBuilder, RowFilterSqlBuilder>();
        services.AddSingleton<IConsentResolutionService, ConsentResolutionService>();
        services.AddSingleton<IIdentitySubjectResolver, IdentitySubjectResolver>();
        services.AddSingleton<IKeyVaultSecretProvider, DefaultEnvironmentSecretProvider>();
        services.AddSingleton<IColumnMaskingProvider, ColumnMaskingProvider>();

        // Outbound SSRF protection (HIGH-03 / SEC-02) & OpenAPI ingestion (P1).
        services.AddHttpClient<IAuditWormExportService, AuditWormExportService>().AddSecureOutboundHandlers(EgressIntegrations.AuditWorm);
        services.AddSingleton<IOpenApiIngestionService, OpenApiIngestionService>();
        services.AddAutherisCatalog();

        // Strategic Enterprise Moats (P10, P11, P12)
        services.AddSingleton<IPolicySimulationService, PolicySimulationService>();
        services.AddSingleton<ISchemaSunsettingService, SchemaSunsettingService>();
        services.AddSingleton<IDifferentialPrivacyEngine, DifferentialPrivacyEngine>();
        services.AddSingleton<IEuAiActAuditExporter, EuAiActAuditExporter>();
        services.AddSingleton<Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService, Autheris.Application.Governance.Services.EffectiveAccessExplanationService>();
        services.AddSingleton<Autheris.Application.Governance.Interfaces.IDeclarativeConsentApplyService, Autheris.Application.Governance.Services.DeclarativeConsentApplyService>();

        // ITSM orchestration (dispatcher, recertification, outbox workers)
        services.AddScoped<ItsmWorkflowDispatcher>();
        services.AddScoped<Autheris.Application.ConsentRequests.IConsentRequestDispatcher, Autheris.Application.ConsentRequests.ItsmConsentRequestDispatcher>();
        services.AddScoped<Autheris.Application.ConsentRequests.IConsentRequestService, Autheris.Application.ConsentRequests.ConsentRequestService>();
        services.AddScoped<IConsentRecertificationService, ConsentRecertificationWorkflowService>();
        if (gatewayOptions.Itsm.Enabled)
        {
            services.AddHostedService<ItsmOutboxDispatcherHostedService>();
            services.AddHostedService<ConsentRecertificationHostedService>();
        }

        // Lineage Graph Store, Impact Analyzer & GDPR Exporter
        services.AddSingleton<ILineageGraphStore, LineageGraphStore>();
        services.AddScoped<ILineageImpactAnalyzerService, LineageImpactAnalyzerService>();
        services.AddSingleton<IGdprAuditReportExporter, GdprAuditReportPdfExporter>();

        // AI Assisted Governance (Triage)
        services.AddScoped<IJustificationTriageService, JustificationTriageService>();

        // Dynamic Schema Contracts (@tag / @inaccessible) (F-GOV-08)
        services.AddSingleton<ISchemaContractManager, SchemaContractManager>();

        // Governance Data Classification Engine & Workflows
        services.AddAutherisDataClassification();

        // DataHub & Data Contract Engine (REQ-DATAHUB-01)
        services.AddSingleton<IDataContractEngine, InMemoryDataContractEngine>();
        services.AddScoped<IDataHubCatalogClient, DataHubCatalogClient>();

        return services;
    }
}
