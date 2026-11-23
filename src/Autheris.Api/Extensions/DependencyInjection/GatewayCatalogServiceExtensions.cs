namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Localization;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class GatewayCatalogServiceExtensions
{
    public static IServiceCollection AddAutherisCatalog(this IServiceCollection services)
    {
        services.AddOptions<CatalogDiscoveryOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(CatalogDiscoveryOptions.SectionName).Bind(o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CatalogDiscoveryOptions>, CatalogDiscoveryOptionsValidator>());

        services.AddOptions<CatalogSearchOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(CatalogSearchOptions.SectionName).Bind(o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CatalogSearchOptions>, CatalogSearchOptionsValidator>());

        services.AddOptions<CatalogLocalizationOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(CatalogLocalizationOptions.SectionName).Bind(o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CatalogLocalizationOptions>, CatalogLocalizationOptionsValidator>());
        services.TryAddSingleton<ILanguageNegotiator, LanguageNegotiator>();

        // State Versioning & Snapshots (INC-138-2, INC-138-4)
        services.TryAddSingleton<ICatalogVersionProvider>(sp =>
            new DbCatalogVersionProvider(
                sp.GetRequiredService<IGovernanceRepository>(),
                sp.GetService<IEventBus>()));
        services.TryAddSingleton<ICatalogSnapshotProvider, CatalogSnapshotProvider>();
        services.TryAddSingleton<ICatalogDirtyFlag, CatalogDirtyFlag>();
        services.TryAddSingleton<ICatalogVersionFlusher>(sp => sp.GetRequiredService<ITableMetadataRepository>());
        services.TryAddSingleton<ICatalogWriteScopeFactory>(sp =>
            new CatalogWriteScopeFactory(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ICatalogDirtyFlag>(),
                sp.GetService<ILogger<CatalogWriteScopeFactory>>(),
                sp.GetService<TimeProvider>()));
        services.AddHostedService<CatalogSnapshotBackgroundService>();

        // Visibility Cache (INC-138-3)
        services.TryAddSingleton<ICatalogVisibilityCache, CatalogVisibilityLruCache>();

        // Honest Embeddings & Search Engine (INC-138-5). The embeddings client follows the outbound egress policy (fail closed).
        services.AddHttpClient(EgressIntegrations.CatalogEmbeddings).AddSecureOutboundHandlers(EgressIntegrations.CatalogEmbeddings);
        services.TryAddSingleton<ICatalogEmbeddingGenerator>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CatalogSearchOptions>>().Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var httpFactory = sp.GetService<System.Net.Http.IHttpClientFactory>();
            var httpClient = httpFactory?.CreateClient(EgressIntegrations.CatalogEmbeddings);
            return CatalogEmbeddingGeneratorFactory.Create(opts, httpClient, loggerFactory);
        });
        services.TryAddSingleton<ILocalEmbeddingGenerator>(sp => sp.GetRequiredService<ICatalogEmbeddingGenerator>());
        services.TryAddSingleton<ICatalogSearchEngine, CatalogSearchEngine>();
        services.AddHostedService<CatalogSearchWarmupService>();

        services.TryAddScoped<ICatalogVisibilityService>(sp =>
            new CatalogVisibilityService(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<IConsentRepository>(),
                sp.GetRequiredService<IOptions<GatewayOptions>>(),
                sp.GetService<ILogger<CatalogVisibilityService>>(),
                sp.GetService<Autheris.Application.DataSources.IIngressTransportContext>(),
                sp.GetService<Autheris.Application.DataSources.IDataSourceOptionsResolver>(),
                sp.GetService<IGovernanceRepository>() is null ? null : sp.GetService<ICatalogSnapshotProvider>()));
        services.TryAddScoped<Autheris.Application.Catalog.Discovery.IDiscoveryArtifactService, Autheris.Application.Catalog.Discovery.DiscoveryArtifactService>();
        services.TryAddScoped<Autheris.Application.Docs.Services.IDocLinkService, Autheris.Application.Docs.Services.DocLinkService>();
        // Optional enrichment: hosts without the document stores (no governance repository) resolve no reader and discovery returns no documents.
        services.TryAddScoped<Autheris.Application.Docs.Services.IDocLinkDiscoveryReader>(sp =>
            sp.GetService<Autheris.Application.Docs.Interfaces.IDocLinkRepository>() is { } links &&
            sp.GetService<Autheris.Application.Docs.Interfaces.IDocumentRepository>() is { } documents &&
            sp.GetService<Autheris.Application.Docs.Interfaces.IUniversalDocStore>() is { } store
                ? new Autheris.Application.Docs.Services.DocLinkDiscoveryReader(links, documents, store)
                : null!);
        services.TryAddScoped<Autheris.Application.Docs.Services.IDocLinkCallerView>(sp =>
            sp.GetService<Autheris.Application.Docs.Services.IDocLinkDiscoveryReader>() is { } reader &&
            sp.GetService<Autheris.Application.Catalog.Discovery.IDiscoveryService>() is { } discovery
                ? new Autheris.Application.Docs.Services.DocLinkCallerView(discovery, reader)
                : null!);
        services.TryAddScoped<Autheris.Application.Catalog.Discovery.IDiscoveryService>(sp =>
            new Autheris.Application.Catalog.Discovery.DiscoveryService(
                sp.GetService<ITableMetadataRepository>(),
                sp.GetService<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>(),
                sp.GetService<IConsentRepository>(),
                sp.GetService<IConsentApprovalRepository>(),
                sp.GetService<IDataOwnershipRepository>(),
                sp.GetService<Autheris.Application.Catalog.Discovery.IDiscoveryArtifactStore>(),
                sp.GetService<Autheris.Application.Audit.IAuditWriter>(),
                sp.GetRequiredService<IOptions<GatewayOptions>>(),
                sp.GetService<IOptions<CatalogDiscoveryOptions>>(),
                sp.GetService<IRateLimiterService>(),
                sp.GetService<ICatalogSearchEngine>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<Autheris.Application.Catalog.Discovery.DiscoveryService>>(),
                sp.GetService<IClassificationRepository>(),
                sp.GetService<Autheris.Application.Procedures.Interfaces.IProcedureCatalog>(),
                sp.GetService<Autheris.Application.Docs.Services.IDocLinkDiscoveryReader>(),
                sp.GetService<IGovernanceRepository>() is null ? null : sp.GetService<ICatalogSnapshotProvider>(),
                sp.GetService<Autheris.Application.Catalog.Discovery.CatalogAccessViewCache>()));

        // Issue #1044: the caller access views of the accessStatus filter, shared across requests (bounded, invalidation-keyed). The
        // invalidation signal exists only with the governance repository; without it the cache is never used.
        services.TryAddSingleton(sp =>
        {
            var governed = sp.GetService<IGovernanceRepository>() is not null;
            return new Autheris.Application.Catalog.Discovery.CatalogAccessViewCache(
                sp.GetService<IOptions<CatalogDiscoveryOptions>>(),
                governed ? sp.GetService<IEpochValidationService>() : null,
                governed ? sp.GetService<Autheris.Application.Catalog.ICatalogVersionProvider>() : null,
                governed ? sp.GetService<Autheris.Application.VirtualFilters.IVirtualFilterSnapshotProvider>() : null,
                sp.GetService<TimeProvider>(),
                sp.GetService<IOptions<GatewayOptions>>(),
                sp.GetService<Autheris.Application.Plugins.Auth.IAuthPluginHost>());
        });
        services.TryAddScoped<Autheris.Application.Catalog.Discovery.IDiscoveryAccessRequestService>(sp =>
            new Autheris.Application.Catalog.Discovery.DiscoveryAccessRequestService(
                sp.GetRequiredService<Autheris.Application.Catalog.Discovery.IDiscoveryService>(),
                sp.GetRequiredService<Autheris.Application.ConsentRequests.IConsentRequestService>(),
                sp.GetRequiredService<IOptions<GatewayOptions>>(),
                sp.GetService<IConsentApprovalRepository>(),
                sp.GetService<IOptions<CatalogDiscoveryOptions>>(),
                sp.GetService<Autheris.Application.Audit.IAuditWriter>(),
                sp.GetService<Autheris.Application.Governance.Grants.IGrantValidityPolicy>()));
        services.TryAddScoped<ICatalogDiscoveryService>(sp => new CatalogDiscoveryService(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<Autheris.Application.Audit.IAuditWriter>(),
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<ICatalogVisibilityService>(),
            sp.GetService<IKeyVaultSecretProvider>(),
            sp.GetService<IOpenApiIngestionService>(),
            sp.GetService<ICatalogSearchEngine>(),
            sp.GetService<ILogger<CatalogDiscoveryService>>(),
            sp.GetService<IOptions<CatalogDiscoveryOptions>>(),
            sp.GetService<System.Net.Http.IHttpClientFactory>(),
            sp.GetService<Autheris.Application.Procedures.Interfaces.IProcedureCatalog>(),
            sp.GetService<IConsentResolutionService>(),
            sp.GetService<IConsentRepository>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<Autheris.Application.DataSources.IDataSourceOptionsResolver>(),
            sp.GetService<Autheris.Application.Mcp.Interfaces.IGraphQlCatalogMap>(),
            sp.GetService<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>() is null
                ? null
                : sp.GetService<Autheris.Application.Catalog.Discovery.IDiscoveryService>()));
        services.TryAddScoped<IPrincipalResolverService, PrincipalResolverService>();
        services.TryAddScoped<IDatasourceTestingService, DatasourceTestingService>();
        services.TryAddScoped<IQueryAnalysisService, QueryAnalysisService>();
        services.TryAddScoped<IGraphQlCostAnalysisEngine, Autheris.GraphQL.Analysis.HotChocolateGraphQlCostAnalysisEngine>();
        services.TryAddScoped<IBundledConsentRequestService, BundledConsentRequestService>();
        services.TryAddSingleton<Autheris.Application.Catalog.External.ICatalogChangedSignal, Autheris.Application.Catalog.External.CatalogChangedSignal>();
        // Issue #1050: single objects come from the catalog snapshot, which exists only with the governance repository (as for discovery).
        services.TryAddScoped(sp => new Autheris.Application.Catalog.External.ExternalCatalogReader(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<Autheris.Application.Catalog.Discovery.IDiscoveryService>(),
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetRequiredService<IDataOwnershipRepository>(),
            sp.GetRequiredService<Autheris.Application.ConsentRequests.IConsentWorkflowConfigRepository>(),
            sp.GetRequiredService<Autheris.Application.Catalog.External.ICatalogChangeStore>(),
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetService<Autheris.Application.Catalog.Discovery.IDiscoveryArtifactStore>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<IOptions<GovernanceTextOptions>>(),
            sp.GetService<IKeyVaultSecretProvider>(),
            sp.GetService<IConfiguration>(),
            sp.GetService<IGovernanceRepository>() is null ? null : sp.GetService<ICatalogSnapshotProvider>()));
        services.AddHostedService<Autheris.Application.Catalog.External.CatalogChangeIndexerHostedService>();
        return services;
    }
}
