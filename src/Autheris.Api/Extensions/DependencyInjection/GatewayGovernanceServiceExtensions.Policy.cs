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
    private static void AddVirtualFiltersAndCallerAttributes(IServiceCollection services)
    {
        services.AddSingleton<IAccessProfileCache, AccessProfileCache>();
        services.AddSingleton<IDbtProposalRepository, InMemoryDbtProposalRepository>();
        services.AddSingleton<IDbtTestTargetMap, DbtTestTargetMap>();
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
        services.AddSingleton<Autheris.Application.Identity.Attributes.CallerAttributeSimulationService>();
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
        // Issue #281: runtime-managed, app-scoped trusted GraphQL documents (registry polls the generation row; the admin service invalidates it).
        services.AddSingleton<Autheris.Application.Governance.TrustedDocuments.ITrustedDocumentAppRegistry, Autheris.Application.Governance.TrustedDocuments.TrustedDocumentAppRegistry>();
        services.AddSingleton<Autheris.Application.Governance.TrustedDocuments.TrustedDocumentAdministrationService>();
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
    }

    private static void AddTypedRowPolicy(IServiceCollection services)
    {
        // WP-X0e: typed row policy. The renderer and the parser are stateless (the parser caches shapes), so one instance serves all.
        services.AddSingleton<Autheris.Application.Sql.Policy.IPolicyPredicateRenderer, Autheris.Application.Sql.Policy.PolicyPredicateRenderer>();
        services.AddSingleton<TrinoSqlEngine.Governance.IPolicyExpressionParser, TrinoSqlEngine.Governance.PolicyExpressionParser>();
        services.AddSingleton<Autheris.Application.Policy.RowPolicyFactory>();
        services.AddSingleton<Autheris.Application.Sql.Masking.SqlDataMaskingProvider>();
        services.AddSingleton<Autheris.Application.Policy.ColumnMaskPolicyFactory>();
        services.AddSingleton<Autheris.Application.Plugins.Auth.AuthPluginRoleEscalationGuard>(sp =>
            new Autheris.Application.Plugins.Auth.AuthPluginRoleEscalationGuard(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Plugins.Auth,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Plugins.Auth.AuthPluginRoleEscalationGuard>>(),
                sp.GetService<Autheris.Application.Audit.IAuditWriter>()));
        services.AddSingleton<Autheris.Infrastructure.Plugins.Auth.GuardedPluginInvoker>();
        services.AddSingleton<Autheris.Application.Plugins.Auth.IAuthPluginHost, Autheris.Infrastructure.Plugins.Auth.AuthPluginHost>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<GatewayOptions>, Autheris.Application.Plugins.Auth.AuthPluginOptionsValidator>();
        services.AddSingleton<Autheris.Application.Policy.TypedPolicySources>();
        // WP-X0l: assembles the typed GovernancePolicy of a statement (no caller until X1). The catalog provider is registered with the storage.
        services.AddSingleton<Autheris.Application.Sql.Policy.GovernancePolicyFactory>();
        services.AddSingleton<IRlsFilterGenerator, RlsFilterGenerator>();
        services.AddSingleton<IRowFilterSqlBuilder, RowFilterSqlBuilder>();
    }
}
