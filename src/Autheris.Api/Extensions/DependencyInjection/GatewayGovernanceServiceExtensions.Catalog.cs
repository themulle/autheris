namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Governance.Ownership;
using Autheris.Application.Audit;
using Autheris.Application.Tenancy;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
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
        // Issue #111: the only writer of dataset tags and discovery flags (REST, manifest and MCP admin tool).
        services.AddSingleton<IDatasetTagImportService, DatasetTagImportService>();
        services.AddSingleton<Autheris.Application.Catalog.Discovery.IDiscoveryArtifactStore>(sp => new DiscoveryArtifactStore(sp.GetRequiredService<Autheris.Application.Common.Interfaces.IGovernanceRepository>()));
        services.AddHostedService<CatalogGovernanceHostedService>();
        services.AddSingleton<Autheris.Application.Governance.Ownership.IContainerOwnership>(sp =>
            new Autheris.Application.Governance.Ownership.ContainerOwnership(
                sp.GetRequiredService<Autheris.Application.Common.Interfaces.ITableMetadataRepository>(),
                sp.GetRequiredService<Autheris.Application.Common.Interfaces.IGovernanceDomainRepository>(),
                sp.GetService<Autheris.Domain.Interfaces.IScimGroupStore>(),
                sp.GetService<Autheris.Application.Identity.IIdentityRealmRegistry>(),
                Autheris.Application.Tenancy.TenantResolver.EffectiveDefaultOf(
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Tenancy)));
        services.AddSingleton<IDatasetCatalogAdminService>(sp => new DatasetCatalogAdminService(
            sp.GetRequiredService<Autheris.Application.Common.Interfaces.ITableMetadataRepository>(),
            sp.GetService<Autheris.Application.Governance.Ownership.IContainerOwnership>(),
            sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<Autheris.Application.Common.Interfaces.IEventBus>(),
            sp.GetService<Autheris.Application.Common.Interfaces.IGovernanceCapabilities>()));
        services.AddSingleton<Autheris.Application.Audit.AuditGroupCommitOptions>();
        services.AddSingleton<Autheris.Application.Audit.AuditGroupCommitSink>();
    }

    private static void AddOpenApiCatalogAndEnterpriseMoats(IServiceCollection services)
    {
        // Outbound SSRF protection (HIGH-03) & OpenAPI ingestion (P1).
        services.AddSingleton<IOpenApiIngestionService, OpenApiIngestionService>();
        services.AddAutherisCatalog();

        // Strategic Enterprise Moats (P10, P11, P12)
        services.AddSingleton<ISchemaSunsettingService, SchemaSunsettingService>();
        services.AddSingleton<Autheris.Application.Policy.Pdp.IExplainSubjectResolver, Autheris.Application.Policy.Pdp.DegradedExplainSubjectResolver>();
        services.AddScoped<Autheris.Application.Policy.Pdp.IAccessExplanationService, Autheris.Application.Policy.Pdp.AccessExplanationService>();
        services.AddScoped<Autheris.Application.Policy.Pdp.IEffectiveAccessService, Autheris.Application.Policy.Pdp.EffectiveAccessService>();
        services.AddScoped<Autheris.Application.Policy.Pdp.IGrantEffectPreview, Autheris.Application.Policy.Pdp.GrantEffectPreviewService>();
        services.AddSingleton<IEuAiActAuditExporter, EuAiActAuditExporter>();
        services.AddSingleton<Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService, Autheris.Application.Governance.Services.EffectiveAccessExplanationService>();
        services.AddOptions<Autheris.Domain.Options.IndividualGrantOptions>() // 16
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((o, cfg) =>
                Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(cfg.GetSection(Autheris.Domain.Options.IndividualGrantOptions.SectionName), o))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<Autheris.Domain.Options.GovernanceTextOptions>() // INC1
            .BindConfiguration(Autheris.Domain.Options.GovernanceTextOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        Autheris.Application.Governance.Grants.IndividualGrantServiceCollectionExtensions.AddIndividualGrantPolicy(services);
        services.AddSingleton<Autheris.Application.Governance.Interfaces.IDeclarativeConsentApplyService, Autheris.Application.Governance.Services.DeclarativeConsentApplyService>();
        services.AddSingleton<Autheris.Application.Governance.Interfaces.IDeclarativeManifestPlanner>(sp =>
            (Autheris.Application.Governance.Interfaces.IDeclarativeManifestPlanner)sp.GetRequiredService<Autheris.Application.Governance.Interfaces.IDeclarativeConsentApplyService>());
        services.AddScoped<Autheris.Application.Audit.AuditReadService>();
    }

    private static void AddTriageAndContracts(IServiceCollection services)
    {
        // Dynamic Schema Contracts (@tag / @inaccessible) (F-GOV-08)
        services.AddSingleton<ISchemaContractManager, SchemaContractManager>();

        // Governance Data Classification Engine & Workflows
        services.AddAutherisDataClassification();
        services.AddAutherisTenancy();

        // Data Contract Engine
        services.AddSingleton<IDataContractEngine, InMemoryDataContractEngine>();
    }
}
