namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
using Autheris.Application.Tenancy;
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

public static partial class GatewayGovernanceServiceExtensions
{
    private static void AddCatalogAndRowFilterAdministration(IServiceCollection services)
    {
        // Issue #107: admin surface of the row filter key-set cache settings and the startup validation of its global options
        // (runs with the GatewayOptions ValidateOnStart; SEC-RF107-16).
        services.AddSingleton<IRowFilterEvaluationAdminService, RowFilterEvaluationAdminService>();

        // Issue #111: the only writer of dataset tags and catalog visibility tiers (REST, manifest and MCP admin tool).
        services.AddSingleton<IDatasetTagImportService, DatasetTagImportService>();
        services.AddSingleton<CatalogVisibilityMetrics>();
        services.AddSingleton<ICatalogVisibilityMetrics>(sp => sp.GetRequiredService<CatalogVisibilityMetrics>());
        services.AddHostedService<CatalogGovernanceHostedService>();
        services.AddSingleton<IDatasetCatalogAdminService>(sp => new DatasetCatalogAdminService(
            sp.GetRequiredService<Autheris.Application.Interfaces.ITableMetadataRepository>(),
            sp.GetService<Autheris.Application.Interfaces.IDataOwnershipRepository>(),
            sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ICatalogVisibilityMetrics>()));
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
    }

    private static void AddOpenApiCatalogAndEnterpriseMoats(IServiceCollection services)
    {
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
        services.AddOptions<Autheris.Domain.Options.GovernanceTextOptions>() // PLAN-GOV-158-INC1
            .BindConfiguration(Autheris.Domain.Options.GovernanceTextOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        Autheris.Application.Governance.Grants.IndividualGrantServiceCollectionExtensions.AddIndividualGrantPolicy(services);
        services.AddSingleton<Autheris.Application.Governance.Interfaces.IDeclarativeConsentApplyService, Autheris.Application.Governance.Services.DeclarativeConsentApplyService>();
    }

    private static void AddTriageContractsAndDataHub(IServiceCollection services)
    {
        // AI Assisted Governance (Triage)
        services.AddScoped<IJustificationTriageService, JustificationTriageService>();

        // Dynamic Schema Contracts (@tag / @inaccessible) (F-GOV-08)
        services.AddSingleton<ISchemaContractManager, SchemaContractManager>();

        // Governance Data Classification Engine & Workflows
        services.AddAutherisDataClassification();
        services.AddAutherisTenancy();

        // DataHub & Data Contract Engine (REQ-DATAHUB-01)
        services.AddSingleton<IDataContractEngine, InMemoryDataContractEngine>();
        services.AddScoped<IDataHubCatalogClient, DataHubCatalogClient>();
    }
}
