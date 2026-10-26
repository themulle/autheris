namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
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
        AddAuditRetention(services);
        AddCatalogAndRowFilterAdministration(services);
        AddGovernanceStateAndStores(services, gatewayOptions);
        AddVirtualFiltersAndCallerAttributes(services);
        AddTypedRowPolicy(services);
        AddRestrictedScopesAndConsentResolution(services);
        AddOpenApiCatalogAndEnterpriseMoats(services);
        AddItsmConsentRequests(services, gatewayOptions);
        AddLineageGdprAndConsumerUsage(services);
        AddTriageContractsAndDataHub(services);
        AddIdentityAndAccessGovernance(services);

        return services;
    }

    private static void AddGovernanceStateAndStores(IServiceCollection services, GatewayOptions gatewayOptions)
    {
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
            ITrustedDocumentRepository,
            IAuditChainExportSource,
            IAuditPurgeKeyProvider,
            ICdcWatermarkRepository,
            Autheris.Application.Dbt.Interfaces.ISemanticManifestStore,
            ICloudEventSubscriptionStore,
            Autheris.Application.Streaming.Interfaces.ICdcStreamStore,
            Autheris.Application.Streaming.Interfaces.ICdcStreamKeyStore,
            IIdentityStoreRepository,
            IFunctionalRightRepository,
            IRecertificationRepository
        where TMigrationRunner : class, IGovernanceMigrationRunner
    {
        services.AddSingleton<TRepository>();
        services.AddSingleton<IGovernanceStoreInitializer>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IGovernanceMigrationRunner, TMigrationRunner>();
        services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<TRepository>());
        // REQ-GOV-129 (A-9): the one write seam for consents checks caller attribute filters against the registry; reads pass through.
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
        services.AddSingleton<IConsentDescriptionRepository>(sp => sp.GetRequiredService<Autheris.Application.Identity.Attributes.AttributeValidatingConsentRepository>()); // PLAN-GOV-158-INC1 (same validating decorator instance as IConsentRepository)
        services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IWebhookOutbox>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IVirtualFilterRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ICallerAttributeRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ITrustedDocumentRepository>(sp => sp.GetRequiredService<TRepository>());
        // WP-SC-03: every engine persists access profiles; the in-memory repository is a test double only.
        services.AddSingleton<IAccessProfileRepository>(sp => (IAccessProfileRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<IIdentityStoreRepository>(sp => (IIdentityStoreRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<IFunctionalRightRepository>(sp => (IFunctionalRightRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<IRecertificationRepository>(sp => (IRecertificationRepository)sp.GetRequiredService<TRepository>());
        services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<ICdcWatermarkRepository>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<IConsumerAggregationRepository>(sp => sp.GetRequiredService<TRepository>());
        // #112 A-9: per-tenant persisted semantic manifest.
        services.AddSingleton<Autheris.Application.Dbt.Interfaces.ISemanticManifestStore>(sp => sp.GetRequiredService<TRepository>());
        // SEC-SEC210-06: register durable CDC stream store and key store from the governance repository
        services.AddSingleton<Autheris.Application.Streaming.Interfaces.ICdcStreamStore>(sp => sp.GetRequiredService<TRepository>());
        services.AddSingleton<Autheris.Application.Streaming.Interfaces.ICdcStreamKeyStore>(sp => sp.GetRequiredService<TRepository>());
        if (cdcWebhooksEnabled)
        {
            services.AddSingleton<ICloudEventSubscriptionStore>(sp => sp.GetRequiredService<TRepository>());
        }
    }
}
