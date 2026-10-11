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
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

public static class GatewayGovernanceServiceExtensions
{
    public static IServiceCollection AddAutherisGovernance(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.PostgreSql))
        {
            services.AddSingleton<PostgreSqlGovernanceRepository>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository, InMemoryAccessProfileRepository>();
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
        }
        else if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.SqlServer))
        {
            services.AddSingleton<SqlServerGovernanceRepository>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository, InMemoryAccessProfileRepository>();
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
        }
        else
        {
            services.AddSingleton<SqliteGovernanceRepository>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        }

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

        // ITSM orchestration (dispatcher, recertification, outbox workers)
        services.AddScoped<ItsmWorkflowDispatcher>();
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

        return services;
    }
}
