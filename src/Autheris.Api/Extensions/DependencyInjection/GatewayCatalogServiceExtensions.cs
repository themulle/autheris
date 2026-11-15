namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Localization;
using Autheris.Application.Catalog.Profiling;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Governance.Analysis;
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

        // Honest Embeddings & Search Engine (INC-138-5)
        services.TryAddSingleton<ICatalogEmbeddingGenerator>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CatalogSearchOptions>>().Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var httpFactory = sp.GetService<System.Net.Http.IHttpClientFactory>();
            var httpClient = httpFactory?.CreateClient("CatalogEmbeddings");
            return CatalogEmbeddingGeneratorFactory.Create(opts, httpClient, loggerFactory);
        });
        services.TryAddSingleton<ILocalEmbeddingGenerator>(sp => sp.GetRequiredService<ICatalogEmbeddingGenerator>());
        services.TryAddSingleton<ICatalogSearchEngine, CatalogSearchEngine>();
        services.AddHostedService<CatalogSearchWarmupService>();

        services.TryAddScoped<ICatalogVisibilityService, CatalogVisibilityService>();
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
                sp.GetService<ILogger<Autheris.Application.Catalog.Discovery.DiscoveryService>>()));
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
        services.TryAddSingleton<IColumnValueProfileService, ColumnValueProfileService>();
        services.TryAddSingleton<ISyntheticSampleRowGenerator, SyntheticSampleRowGenerator>();
        services.TryAddScoped<IQueryAnalysisService, QueryAnalysisService>();
        services.TryAddScoped<IGraphQlCostAnalysisEngine, Autheris.GraphQL.Analysis.HotChocolateGraphQlCostAnalysisEngine>();
        services.TryAddScoped<IBundledConsentRequestService, BundledConsentRequestService>();
        services.TryAddSingleton<Autheris.Application.Catalog.External.ICatalogChangedSignal, Autheris.Application.Catalog.External.CatalogChangedSignal>();
        services.TryAddScoped<Autheris.Application.Catalog.External.ExternalCatalogReader>();
        services.AddHostedService<Autheris.Application.Catalog.External.CatalogChangeIndexerHostedService>();
        return services;
    }
}
