using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Federation;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Idempotency;
using Autheris.Infrastructure.Messaging;
using Autheris.Extensions;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Infrastructure.Security;
using Autheris.Api.Security;
using Autheris.Application.Plugins;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Lineage;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Runtime.ResourceGroups;
using Autheris.Application.Observability;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Plugins;
using Autheris.Domain.Diagnostics;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Infrastructure.Serialization;
using Autheris.Application.State.Caching.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Infrastructure.Streaming;
using Autheris.GraphQL.Subscriptions;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Sql;
using Autheris.Application.Common.Serialization;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using StackExchange.Redis;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Autheris.Api.Configuration.Validation;

namespace Autheris.Api.Extensions;

public static partial class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// I417-SC-A03: builds the admission rule from the same <c>GatewayOptions.QueryCost</c> instance that
    /// <c>CostAndQuotaMiddleware</c> reads, so both price a list identically.
    /// </summary>
    /// <summary>
    /// Issue #786: the module is composed here with the catalog version provider, the epoch service and the clock, so an idle
    /// refresh tick exits on the unchanged version instead of rebuilding the whole candidate model every interval.
    /// Issue #802: the event bus delivers <c>catalog:changed</c>, so a committed catalog write starts a prompt check.
    /// </summary>
    internal static CatalogGraphQlTypeModule CreateCatalogGraphQlTypeModule(IServiceProvider sp, GatewayOptions gatewayOptions) =>
        new(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<ITableRelationRepository>(),
            sp.GetService<ILogger<CatalogGraphQlTypeModule>>(),
            TimeSpan.FromSeconds(gatewayOptions.GraphQL.CatalogSchemaRefreshSeconds),
            versionProvider: sp.GetService<Autheris.Application.Catalog.ICatalogVersionProvider>(),
            aggregationEnabled: true,
            crossSourceEnabled: gatewayOptions.IsCrossSourceEnabled,
            metricsEnabled: true,
            graphQlOptions: gatewayOptions.GraphQL,
            transportResolver: sp.GetService<Autheris.Application.DataSources.IDataSourceOptionsResolver>(),
            epochService: sp.GetService<IEpochValidationService>(),
            timeProvider: sp.GetService<TimeProvider>(),
            eventBus: sp.GetService<Autheris.Application.Common.Interfaces.IEventBus>(),
            snapshotProvider: sp.GetService<IGovernanceRepository>() is null ? null : sp.GetService<Autheris.Application.Catalog.ICatalogSnapshotProvider>());

    internal static Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule CreateQueryCostRule(
        GatewayOptions gatewayOptions,
        int maxCost,
        Autheris.Domain.Options.AnalyticsOptions? analytics,
        Autheris.GraphQL.Catalog.ICatalogRelationRegistry? relationRegistry = null) =>
        new(
            maxAllowedCost: maxCost,
            maxResponseRows: gatewayOptions.GraphQL.MaxResponseRows,
            onQueryTooComplex: () => GatewayDiagnostics.QueryTooComplexCounter.Add(1),
            maxRootFields: gatewayOptions.AreQueryLimitsRelaxed
                ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 200 : 25)
                : gatewayOptions.GraphQL.MaxRootFieldsPerOperation,
            maxExecutionDepth: Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule.ResolveMaxExecutionDepth(gatewayOptions.GraphQL),
            costOptions: gatewayOptions.QueryCost ?? throw new InvalidOperationException("Gateway:QueryCost must not be null."),
            aggregateFieldCostWeight: analytics?.Aggregation.AggregateFieldCostWeight
                ?? Autheris.Domain.Options.AggregationOptions.DefaultAggregateFieldCostWeight,
            maxGroupByColumns: analytics?.Aggregation.MaxGroupByColumns
                ?? Autheris.Domain.Options.AggregationOptions.DefaultMaxGroupByColumns,
            maxAggregates: analytics?.Aggregation.MaxAggregates
                ?? Autheris.Domain.Options.AggregationOptions.DefaultMaxAggregates,
            maxAggregateFieldsPerOperation: gatewayOptions.GraphQL.Aggregation.MaxFieldsPerOperation,
            relationRegistry: relationRegistry);

    public static IServiceCollection AddGatewayGraphQL(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        // RR-L3-05: In production without explicit opt-in, relaxed limits are capped to moderate thresholds
        var maxDepth = gatewayOptions.AreQueryLimitsRelaxed
            ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100 : 15)
            : gatewayOptions.GraphQL.MaxAllowedExecutionDepth;
        var maxCost = Autheris.GraphQL.Interceptors.CostAndQuotaMiddleware.ResolveMaxAdmissionCeiling(gatewayOptions);

        services.AddSingleton<Autheris.GraphQL.Catalog.ICatalogRelationRegistry>(Autheris.GraphQL.Catalog.CatalogRelationRegistry.Current);
        // M-16: Singleton so registered API keys and the key cache persist across requests.
        services.AddSingleton<IClientTierResolver, ClientTierResolver>();
        services.AddSingleton<Autheris.Application.Common.Quota.IQueryCostQuota, Autheris.Application.Common.Quota.QueryCostQuota>();
        services.AddSingleton<Autheris.Application.Common.Quota.IScopeQueryExecutionGuard, Autheris.Application.Common.Quota.ScopeQueryExecutionGuard>();
        services.AddHttpClient("OpenApiIngestion").AddSecureOutboundHandlers(EgressIntegrations.OpenApiIngestion);

        services.AddFusionFederationServices(gatewayOptions);

        services.AddSingleton<ErrorSanitizingFilter>();
        // R-GQL-12: one module instance (and change-detection timer) for the application, also across schema rebuilds.
        services.AddSingleton(sp => CreateCatalogGraphQlTypeModule(sp, gatewayOptions));
        services.AddSingleton<Autheris.GraphQL.Catalog.CatalogSchemaBuildDiagnosticListener>();
        services.AddSingleton<ICatalogSchemaDiagnostics>(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>());
        services.AddSingleton<Autheris.GraphQL.Catalog.ICatalogSchemaFreshness>(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>());
        services.AddSingleton(sp => new Autheris.Api.Endpoints.SchemaStatusAuditSampler(sp.GetService<TimeProvider>()));
        services.AddSingleton<ISocketTokenValidator, JwtSocketTokenValidator>();
        services.AddOptions<RevocationWatchOptions>()
            .BindConfiguration(RevocationWatchOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<RevocationWatchOptions>, RevocationWatchOptionsValidator>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, RequireFunctionalRightHandler>();
        services.AddOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>()
            .BindConfiguration(Autheris.Domain.Options.Security.IdentityRealmsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>, Autheris.Application.Identity.Validators.IdentityRealmOptionsValidator>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>, Autheris.Application.Identity.Validators.IdentityRealmsScimOptionsValidator>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>, Autheris.Application.Identity.Validators.SubjectRoleOptionsValidator>();
        // DT-A-11 / DT-SC-14: enabled schemes need explicit realms; a realm without binding needs a default tenant.
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>>(
            new Autheris.Application.Identity.Validators.IdentityRealmSchemeValidator(gatewayOptions));
        services.AddSingleton<WebSocketAuthInterceptor>();

        var gqlBuilder = services
            .AddGraphQLServer()
            .UseInstrumentation()
            .UseExceptions()
            // R-GQL-6: wraps validation and execution so unknown and denied fields yield the same result.
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseTimeout()
            .UseDocumentCache();

        // SEC H-08: Trusted-document enforcement must run BEFORE parsing/validation/execution.
        // HotChocolate's UseOnlyPersistedOperationAllowed() was previously appended after UseOperationExecution
        // without a document store and without OnlyAllowPersistedDocuments, i.e. it never took effect.
        // We enforce an allowlist of trusted documents (normalized SHA-256) directly after the document cache.
        if (gatewayOptions.GraphQL.PersistedQueriesOnly)
        {
            // Issue #281: an empty global directory is valid when app-scoped trusted documents carry the allowlist.
            var trustedDocuments = gatewayOptions.GraphQL.AppScopedTrustedDocuments.Enabled && string.IsNullOrWhiteSpace(gatewayOptions.GraphQL.TrustedDocumentsDirectory)
                ? new TrustedDocumentStore([])
                : TrustedDocumentStore.LoadFromDirectory(gatewayOptions.GraphQL.TrustedDocumentsDirectory, allowEmpty: gatewayOptions.GraphQL.AppScopedTrustedDocuments.Enabled);
            services.AddSingleton(trustedDocuments);
            gqlBuilder.UseRequest<TrustedDocumentsOnlyMiddleware>();
        }

        gqlBuilder
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<Autheris.GraphQL.Interceptors.ReadOnlyOperationMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.DbtHealthExecutionMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.SchemaSunsettingExecutionMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.MaskedFieldsMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.SlowListsMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.DatasetFactsMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.PaginationTruncationMiddleware>()
            .TryAddTypeInterceptor<Autheris.GraphQL.Catalog.CatalogSchemaBuildProgressTypeInterceptor>()
            .TryAddTypeInterceptor<Autheris.GraphQL.Federation.SubgraphFieldMaskingTypeInterceptor>()
            .UseRequest<Autheris.GraphQL.Catalog.CatalogOperationCleanupMiddleware>()
            .UseOperationCache()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseRequest<Autheris.GraphQL.Interceptors.CostAndQuotaMiddleware>()
            // Issue #447: transport gate for every root field of the compiled operation, before any resolver runs.
            .UseRequest<Autheris.GraphQL.Interceptors.TransportGateMiddleware>()
            .UseOperationExecution()
            .UseField(next => new Autheris.GraphQL.Interceptors.TransportIngressFieldMiddleware(next).InvokeAsync)
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddApplicationService<WebSocketAuthInterceptor>()
            .AddApplicationService<ITableMetadataRepository>()
            .AddApplicationService<ITableRelationRepository>()
            .AddApplicationService<CatalogGraphQlTypeModule>()
            .AddApplicationService<Autheris.GraphQL.Catalog.CatalogSchemaBuildDiagnosticListener>()
            .AddDiagnosticEventListener(sp => sp.GetRequiredService<Autheris.GraphQL.Catalog.CatalogSchemaBuildDiagnosticListener>())
            .AddApplicationService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()
            .AddTypeModule(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>())
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddGatewaySubscriptions(gatewayOptions.GraphQL)
            .AddDirectiveType<Autheris.GraphQL.Directives.McpToolDirectiveType>()
            .AddMaxExecutionDepthRule(maxDepth)
            .AddValidationRule<Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule>((sp, _) =>
                CreateQueryCostRule(
                    gatewayOptions,
                    maxCost,
                    sp.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value,
                    sp.GetService<Autheris.GraphQL.Catalog.ICatalogRelationRegistry>()))
            // HotChocolate 16 adds the types __SchemaDefinition/__SearchResult (semantic introspection) to the schema.
            // Their names start with "__", which GraphQL reserves for introspection; graphql-js based clients (GraphiQL,
            // Apollo, codegen) reject the whole schema because of them. Autheris does not use the semantic search.
            // Issue #1109: HotChocolate's own warm-up hosted service would await the full executor build inside the host
            // start, so Kestrel (and /health/live) would only listen after it. The build is started in the background by
            // GraphQlExecutorGate and McpSchemaDiscoveryService instead; /health/ready stays 503 (Warming) until both are done,
            // and a request that arrives earlier waits for the same build (no partial schema). Warm-up tasks still run on
            // every executor creation.
            .ModifyOptions(opt =>
            {
                opt.EnableSemanticIntrospection = false;
                opt.LazyInitialization = true;
            })
            .ModifyRequestOptions(opt =>
            {
                opt.ExecutionTimeout = TimeSpan.FromSeconds(gatewayOptions.HighAvailability.QueryTimeoutSeconds);
            });

        // SEC H-02: Outside explicit configuration, introspection is disabled.
        if (!gatewayOptions.GraphQL.EnableIntrospection && !gatewayOptions.IsIntrospectionForced)
        {
            gqlBuilder.DisableIntrospection();

            // R-GQL-6: GET /graphql?sdl and /graphql/schema.graphql serve the SDL independently of introspection.
            gqlBuilder.ModifyServerOptions(opt => opt.EnableSchemaRequests = false);
        }

        // GHSA-cj7m-c75j-6p6h: GraphQL request batching setting
        gqlBuilder.ModifyServerOptions(opt => opt.Batching = HotChocolate.AspNetCore.AllowedBatching.None);

        return services;
    }
}
