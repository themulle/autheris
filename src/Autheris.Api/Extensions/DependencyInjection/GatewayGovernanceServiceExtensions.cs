namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Events.Interfaces;
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

        // Issue #107: admin surface of the row filter key-set cache settings and the startup validation of its global options
        // (runs with the GatewayOptions ValidateOnStart; SEC-RF107-16).
        services.AddSingleton<IRowFilterEvaluationAdminService, RowFilterEvaluationAdminService>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<GatewayOptions>, Autheris.Application.Policy.RowFilterKeySetCacheOptionsValidator>();
        // Issue #107 (WP-C): the row-filter key-set cache runtime. The cache size and concurrency are read once (a size change needs a restart,
        // SEC-RF107-16); every other option is read per statement by the stage through IOptionsMonitor.
        services.AddSingleton<TrinoSqlEngine.Governance.IPolicyKeyQueryCompiler>(sp =>
            sp.GetRequiredService<TrinoSqlEngine.ISqlEngine>() as TrinoSqlEngine.Governance.IPolicyKeyQueryCompiler ?? new TrinoSqlEngine.Governance.PolicyKeyQueryCompiler());
        services.AddSingleton(sp =>
        {
            var keySets = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.RowFilters.KeySetCache;
            var cache = new Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetCache(
                keySets.SizeLimitBytes, keySets.MaxConcurrentKeyQueries, sp.GetService<TimeProvider>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetCache>>());
            Autheris.Domain.Diagnostics.GatewayDiagnostics.Meter.CreateObservableGauge(
                "autheris.rowfilter.keyset.cache.size_bytes", () => cache.CurrentEstimatedSize, "By", "Estimated bytes of the row-filter key-set cache");
            return cache;
        });
        services.AddSingleton<Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetLoader>();
        services.AddSingleton<Autheris.Application.Sql.Policy.RowFilterKeySets.IRowFilterKeySetLoader>(sp =>
            sp.GetRequiredService<Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetLoader>());
        services.AddSingleton<Autheris.Application.Sql.Policy.RowFilterKeySets.IRowFilterKeySetStage, Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetStage>();
        services.AddHostedService<Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetStartupReporter>();
        services.AddSingleton<Autheris.Application.Auditing.AuditGroupCommitOptions>();
        services.AddSingleton<Autheris.Application.Auditing.AuditGroupCommitSink>();

        // WP-ARC-A-03: the durable CDC webhook subscription store is a view of the governance repository (only when the feature is on).
        var cdcWebhooksEnabled = CdcWebhooksFeature.IsEnabled(services);

        // WP-ARC-B-13: one state directory for the SQLite governance database and the audit anchor next to it.
        services.AddOptions<StateOptions>().BindConfiguration(StateOptions.SectionName).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<StateOptions>, StateOptionsValidator>();
        services.AddSingleton<IGatewayStatePaths, GatewayStatePaths>();

        // ARC-I-13: the anchor store is a required repository dependency; AU-01/AU-03 are enforced when it is built.
        services.AddSingleton<IAuditChainAnchorStore>(sp => AuditChainAnchorStoreFactory.CreateForGovernance(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            sp.GetRequiredService<IKeyVaultSecretProvider>(),
            sp.GetService<IAuditAnchorSigner>(),
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Autheris.Infrastructure.Persistence.AuditChainAnchorStore"),
            sp.GetRequiredService<IGatewayStatePaths>()));

        if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.PostgreSql))
        {
            RegisterGovernanceAggregateStores<PostgreSqlGovernanceRepository, PostgreSqlGovernanceMigrationRunner>(
                services,
                cdcWebhooksEnabled);
        }
        else if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.SqlServer))
        {
            RegisterGovernanceAggregateStores<SqlServerGovernanceRepository, SqlServerGovernanceMigrationRunner>(
                services,
                cdcWebhooksEnabled);
        }
        else
        {
            RegisterGovernanceAggregateStores<SqliteGovernanceRepository, SqliteGovernanceMigrationRunner>(
                services,
                cdcWebhooksEnabled);
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
        // REQ-GOV-129: caller attribute registry and its administration (four-eyes).
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeProvider, Autheris.Application.Identity.Attributes.ClaimsAttributeProvider>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeProvider, Autheris.Application.Identity.Attributes.ServiceAttributeProvider>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeProvider, Autheris.Application.Identity.Attributes.AttributeTableProvider>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeDefinitionValidator, Autheris.Application.Identity.Attributes.ClaimsAttributeDefinitionValidator>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeDefinitionValidator, Autheris.Application.Identity.Attributes.AttributeTableDefinitionValidator>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.CallerAttributeResolver>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeResolver>(sp => sp.GetRequiredService<Autheris.Application.Identity.Attributes.CallerAttributeResolver>());
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeCacheInvalidator>(sp => sp.GetRequiredService<Autheris.Application.Identity.Attributes.CallerAttributeResolver>());
        services.AddSingleton<Autheris.Application.Identity.Attributes.ICallerAttributeRegistry, Autheris.Application.Identity.Attributes.CallerAttributeRegistry>();
        services.AddSingleton<Autheris.Application.Identity.Attributes.CallerAttributeAdministrationService>(sp => new Autheris.Application.Identity.Attributes.CallerAttributeAdministrationService(
            sp.GetRequiredService<Autheris.Domain.Interfaces.ICallerAttributeRepository>(),
            sp.GetRequiredService<Autheris.Application.Identity.Attributes.ICallerAttributeRegistry>(),
            sp.GetRequiredService<Autheris.Application.Interfaces.IAuditLogRepository>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetServices<Autheris.Application.Identity.Attributes.ICallerAttributeDefinitionValidator>()));
        services.AddSingleton<IVirtualFilterPredicateBuilder, StructuredFilterSqlBuilder>();
        services.AddSingleton<MandatoryRowFilterResolver>();
        services.AddSingleton<IMandatoryRowFilterResolver>(sp => sp.GetRequiredService<MandatoryRowFilterResolver>());
        services.AddSingleton<IEffectiveFilterInspector, EffectiveFilterInspector>();
        services.AddScoped<Autheris.Application.Governance.IOpenApiIngestionUseCase, Autheris.Application.Governance.OpenApiIngestionUseCase>();
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
        // WP-0 (PLAN-GOV-113): only a person decides consent requests. The resolvers resolve the guard with GetRequiredService (no fallback).
        services.AddSingleton<Autheris.Application.Governance.ConsentRequests.IConsentDecisionGuard, Autheris.Application.Governance.ConsentRequests.ConsentDecisionGuard>();
        services.AddSingleton<Autheris.Application.Governance.ConsentRequests.IConsentDecisionDenialRecorder, Autheris.Application.Governance.ConsentRequests.ConsentDecisionDenialRecorder>();
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
        services.AddOptions<Autheris.Domain.Options.IndividualGrantOptions>() // WP-SC-16
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((o, cfg) =>
                Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(cfg.GetSection(Autheris.Domain.Options.IndividualGrantOptions.SectionName), o))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        Autheris.Application.Governance.Grants.IndividualGrantServiceCollectionExtensions.AddIndividualGrantPolicy(services);
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

    private static void RegisterGovernanceAggregateStores<TRepository, TMigrationRunner>(
        IServiceCollection services,
        bool cdcWebhooksEnabled)
        where TRepository : class,
            IGovernanceStoreInitializer,
            IGovernanceRepository,
            ITableMetadataRepository,
            IConsentRepository,
            IAuditLogRepository,
            IPolicyEpochRepository,
            IConsentApprovalRepository,
            IDataOwnershipRepository,
            ITableRelationRepository,
            IItsmOutboxRepository,
            IWebhookOutbox,
            IVirtualFilterRepository,
            ICallerAttributeRepository,
            IAuditChainExportSource,
            ICdcWatermarkRepository,
            ICloudEventSubscriptionStore
        where TMigrationRunner : class, IGovernanceMigrationRunner
    {
        services.AddSingleton<TRepository>();
        services.AddSingleton<IGovernanceStoreInitializer>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IGovernanceMigrationRunner, TMigrationRunner>();
        services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IWebhookOutbox>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ICallerAttributeRepository>(sp => sp.GetRequiredService<TRepository>());
        // WP-SC-03: every engine persists access profiles; the in-memory repository is a test double only.
        services.AddSingleton<IAccessProfileRepository>(sp => (IAccessProfileRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ICdcWatermarkRepository>(sp => sp.GetRequiredService<TRepository>());
        if (cdcWebhooksEnabled)
        {
            services.AddSingleton<ICloudEventSubscriptionStore>(sp => sp.GetRequiredService<TRepository>());
        }
    }
}
