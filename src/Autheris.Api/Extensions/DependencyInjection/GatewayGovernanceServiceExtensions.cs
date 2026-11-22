namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
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
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;

public static partial class GatewayGovernanceServiceExtensions
{
    /// <summary>
    /// Registers the hosted service that migrates and initializes the governance store (decision G-8). Must be called
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
        AddAuditRetention(services);
        AddCatalogAndRowFilterAdministration(services);
        AddGovernanceStateAndStores(services, gatewayOptions);
        AddVirtualFiltersAndCallerAttributes(services);
        AddTypedRowPolicy(services);
        AddRestrictedScopesAndConsentResolution(services);
        AddOpenApiCatalogAndEnterpriseMoats(services);
        AddConsentRequests(services);
        AddLineageGdprAndConsumerUsage(services);
        AddTriageAndContracts(services);
        AddIdentityAndAccessGovernance(services);
        AddDataProducts(services);
        AddAccessPolicyAndDomainServices(services);
        AddGovernanceProtectionCaching(services);

        return services;
    }

    private static void AddAccessPolicyAndDomainServices(IServiceCollection services)
    {
        services.AddOptions<AccessPolicyOptions>()
            .BindConfiguration(AccessPolicyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<GovernanceNotificationOptions>()
            .BindConfiguration(GovernanceNotificationOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<AccessPolicyOptions>, Autheris.Application.Governance.Policy.AccessPolicyOptionsValidator>();
        services.AddDataProtection().SetApplicationName("Autheris.Gateway");
        services.AddSingleton<Autheris.Application.Governance.Notifications.INotificationWebhookUrlProtector, DataProtectionNotificationWebhookUrlProtector>();
        services.AddSingleton<Autheris.Application.Governance.Notifications.AdaptiveCardPayloadFormatter>();
        services.AddSingleton<Autheris.Application.Governance.Policy.AccessPolicySettingsProvider>();
        services.AddSingleton<Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider>(sp =>
            sp.GetRequiredService<Autheris.Application.Governance.Policy.AccessPolicySettingsProvider>());
        services.AddHostedService<Autheris.Application.Governance.Notifications.NotificationChannelStartupAuditHostedService>();
        services.AddSingleton<Autheris.Application.Governance.Domains.GovernanceDomainService>(sp =>
            new Autheris.Application.Governance.Domains.GovernanceDomainService(
                sp.GetRequiredService<IGovernanceDomainRepository>(),
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<IConsentRepository>(),
                sp.GetRequiredService<IAuditWriter>(),
                sp.GetRequiredService<IGovernanceCapabilities>(),
                sp.GetRequiredService<Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider>(),
                sp.GetRequiredService<Autheris.Application.Governance.Protection.ProtectionSnapshotProvider>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<Autheris.Application.Products.IDataProductRepository>(),
                sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()));
    }

    private static void AddGovernanceProtectionCaching(IServiceCollection services)
    {
        services.AddOptions<GovernanceCacheSettings>()
            .BindConfiguration(GovernanceCacheSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<Autheris.Application.Governance.Caching.IGovernanceChangeNotifier, RedisGovernanceChangeChannel>();
        services.AddSingleton<Autheris.Application.Governance.Caching.GovernanceEpochProvider>();
        services.AddSingleton<Autheris.Application.Governance.Caching.IGovernanceCacheInvalidator>(sp =>
            sp.GetRequiredService<Autheris.Application.Governance.Caching.GovernanceEpochProvider>());
        services.AddSingleton<Autheris.Application.Governance.Protection.ProtectionSnapshotProvider>(sp =>
            new Autheris.Application.Governance.Protection.ProtectionSnapshotProvider(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<IGovernanceDomainRepository>(),
                sp.GetRequiredService<Autheris.Application.Governance.Caching.GovernanceEpochProvider>(),
                sp.GetService<Autheris.Application.Catalog.ICatalogVersionProvider>()
                    ?? new Autheris.Infrastructure.Persistence.Catalog.DbCatalogVersionProvider(
                        sp.GetRequiredService<ITableMetadataRepository>(),
                        sp.GetService<IEventBus>(),
                        timeProvider: sp.GetService<TimeProvider>()),
                sp.GetService<Microsoft.Extensions.Options.IOptions<GovernanceCacheSettings>>(),
                sp.GetService<Autheris.Application.Governance.Caching.IGovernanceChangeNotifier>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<Autheris.Application.Products.IDataProductRepository>()));
        services.AddSingleton<IProtectionSnapshotProvider>(sp =>
            sp.GetRequiredService<Autheris.Application.Governance.Protection.ProtectionSnapshotProvider>());
        services.AddSingleton<Autheris.Application.Governance.Caching.IGovernanceCacheInvalidator>(sp =>
            sp.GetRequiredService<Autheris.Application.Governance.Protection.ProtectionSnapshotProvider>());
        services.AddHostedService<Autheris.Application.Governance.Caching.GovernanceCacheInvalidationHostedService>();
    }

    private static void AddGovernanceStateAndStores(IServiceCollection services, GatewayOptions gatewayOptions)
    {
        // the durable CDC webhook subscription store is a view of the governance repository (only when the feature is on).
        var cdcWebhooksEnabled = CdcWebhookStoreFeature.IsEnabled(services);

        // one state directory (Gateway:State:StateDirectory) for the gateway state files.
        services.AddOptions<StateOptions>().BindConfiguration(StateOptions.SectionName).ValidateOnStart();
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
            services.AddSingleton<IGovernanceSqlDialect>(sp => new PostgreSqlGovernanceDialect(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.GovernanceDb.ConnectionString));
            RegisterGovernanceAggregateStores<GovernanceRepository, PostgreSqlGovernanceMigrationRunner>(
                services,
                cdcWebhooksEnabled);
        }
        else if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.SqlServer))
        {
            services.AddSingleton<IGovernanceSqlDialect>(sp => new SqlServerGovernanceDialect(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.GovernanceDb.ConnectionString));
            RegisterGovernanceAggregateStores<GovernanceRepository, SqlServerGovernanceMigrationRunner>(
                services,
                cdcWebhooksEnabled);
        }
        else
        {
            throw new InvalidOperationException("Governance provider unsupported (OD-1). See docs/operations/migration-simplification.md.");
        }

        // durable, fenced CDC watermarks live in the governance database (the extension registers no store).
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
            IDataOwnerAssignmentRepository,
            ITableRelationRepository,
            IWebhookOutbox,
            IVirtualFilterRepository,
            ICallerAttributeRepository,
            ITrustedDocumentRepository,
            IAuditChainExportSource,
            IAuditPurgeKeyProvider,
            ICdcWatermarkRepository,
            Autheris.Application.Catalog.Semantic.ISemanticManifestStore,
            ICloudEventSubscriptionStore,
            Autheris.Application.Streaming.Interfaces.ICdcStreamStore,
            Autheris.Application.Streaming.Interfaces.ICdcStreamKeyStore,
            Autheris.Application.ConsentRequests.IConsentWorkflowConfigRepository,
            Autheris.Application.ConsentRequests.Routing.IConsentWorkflowAdminRepository,
            Autheris.Application.ConsentRequests.Events.IConsentEventOutbox,
            Autheris.Application.Governance.Grants.IGrantExpiryStore,
            IIdentityStoreRepository,
            IFunctionalRightRepository,
            Autheris.Application.Usage.IUsageRepository,
            Autheris.Application.Catalog.External.ICatalogChangeStore,
            IClassificationRepository,
            Autheris.Application.Governance.Grants.IOwnerGrantStore,
            Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentStore,
            Autheris.Application.Docs.Interfaces.IDocumentRepository,
            Autheris.Application.Mcp.Interfaces.IGoldenQueryRepository,
            Autheris.Application.Products.IDataProductRepository,
            Autheris.Application.Docs.Interfaces.IDocLinkRepository
        where TMigrationRunner : class, IGovernanceMigrationRunner
    {
        services.AddSingleton<TRepository>();
        services.AddSingleton<IGovernanceStoreInitializer>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IGovernanceMigrationRunner, TMigrationRunner>();
        services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<TRepository>());
        // 129 (A-9): the one write seam for consents checks caller attribute filters against the registry; reads pass through.
        // GOV-12: one materialisation guard (A-10 + registry rules) shared by this write seam, the approval materialisation in the
        // governance repositories and the request creation services. The registry and the table repository are resolved lazily because
        // the repositories (which take the guard) are the backing store of both.
        services.AddSingleton<Autheris.Application.Identity.Attributes.IConsentMaterializationGuard>(sp => new Autheris.Application.Identity.Attributes.CallerAttributeConsentGuard(
            () => sp.GetRequiredService<Autheris.Application.Identity.Attributes.ICallerAttributeRegistry>(),
            () => sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>()));
        services.AddSingleton(sp => new Autheris.Application.Identity.Attributes.AttributeValidatingConsentRepository(
            sp.GetRequiredService<TRepository>(),
            sp.GetRequiredService<Autheris.Application.Identity.Attributes.IConsentMaterializationGuard>()));
        services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<Autheris.Application.Identity.Attributes.AttributeValidatingConsentRepository>());
        services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IAuditPurgeKeyProvider>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.ConsentRequests.IConsentWorkflowConfigRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.ConsentRequests.Routing.IConsentWorkflowAdminRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.ConsentRequests.Events.IConsentEventOutbox>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Governance.Grants.IGrantExpiryStore>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IConsentDescriptionRepository>(sp => sp.GetRequiredService<Autheris.Application.Identity.Attributes.AttributeValidatingConsentRepository>()); // INC1 (same validating decorator instance as IConsentRepository)
        services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IDataOwnerAssignmentRepository>(sp => sp.GetRequiredService<TRepository>()); // issue #393 part b: dbt import owner write seam
        services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IWebhookOutbox>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ICallerAttributeRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITrustedDocumentRepository>(sp => sp.GetRequiredService<TRepository>());
        // every engine persists access profiles; the in-memory repository is a test double only.
        services.AddSingleton<IAccessProfileRepository>(sp => (IAccessProfileRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<IIdentityStoreRepository>(sp => (IIdentityStoreRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Governance.Grants.IOwnerGrantStore>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentStore>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IFunctionalRightRepository>(sp => (IFunctionalRightRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Catalog.External.ICatalogChangeStore>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IClassificationRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Docs.Interfaces.IDocumentRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Mcp.Interfaces.IGoldenQueryRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Products.IDataProductRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Docs.Interfaces.IDocLinkRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<GovernanceDomainStore>();
        services.AddSingleton<IGovernanceDomainRepository>(sp => sp.GetRequiredService<GovernanceDomainStore>());
        services.AddSingleton<Autheris.Application.Governance.Caching.IGovernanceEpochStore>(sp => sp.GetRequiredService<GovernanceDomainStore>());
        services.AddSingleton<CatalogVisibilityGrantStore>(sp =>
            new CatalogVisibilityGrantStore(
                sp.GetRequiredService<IGovernanceSqlDialect>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<Autheris.Application.Governance.Caching.IGovernanceChangeNotifier>()));
        services.AddSingleton<ICatalogVisibilityGrantRepository>(sp => sp.GetRequiredService<CatalogVisibilityGrantStore>());
        services.AddSingleton<AccessPolicySettingsStore>(sp =>
            new AccessPolicySettingsStore(
                sp.GetRequiredService<IGovernanceSqlDialect>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<Autheris.Application.Governance.Caching.IGovernanceChangeNotifier>()));
        services.AddSingleton<IAccessPolicySettingsRepository>(sp => sp.GetRequiredService<AccessPolicySettingsStore>());
        services.AddSingleton<MigrationPurgeStore>(sp =>
            new MigrationPurgeStore(
                sp.GetRequiredService<IGovernanceSqlDialect>(),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<Autheris.Infrastructure.Health.GovernanceSchemaVersionReadinessCheck>();
        services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ICdcWatermarkRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IConsumerAggregationRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Usage.IUsageRepository>(sp => sp.GetRequiredService<TRepository>());
        // #112 A-9: per-tenant persisted semantic manifest.
        services.AddSingleton<Autheris.Application.Catalog.Semantic.ISemanticManifestStore>(sp => sp.GetRequiredService<TRepository>());
        // register durable CDC stream store and key store from the governance repository
        services.AddSingleton<Autheris.Application.Streaming.Interfaces.ICdcStreamStore>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Streaming.Interfaces.ICdcStreamKeyStore>(sp => sp.GetRequiredService<TRepository>());
        if (cdcWebhooksEnabled)
        {
            services.AddSingleton<ICloudEventSubscriptionStore>(sp => sp.GetRequiredService<TRepository>());
        }
    }
}
