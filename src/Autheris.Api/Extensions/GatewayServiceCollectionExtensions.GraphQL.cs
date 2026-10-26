using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
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
using Autheris.Application.Workflows;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.ResourceGroups;
using Autheris.Application.Observability;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Plugins;
using Autheris.Domain.Diagnostics;
using Autheris.Application.Caching.Interfaces;
using Autheris.Infrastructure.Garnet;
using Autheris.Infrastructure.Serialization;
using Autheris.Application.Caching.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Infrastructure.Streaming;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Cdn;
using Autheris.Application.SchemaRegistry;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Sql;
using Autheris.Application.Serialization;
using Autheris.Application.SchemaRegistry.Validation;
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
    public static IServiceCollection AddGatewayGraphQL(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        // RR-L3-05: In production without explicit opt-in, relaxed limits are capped to moderate thresholds
        var maxDepth = gatewayOptions.AreQueryLimitsRelaxed
            ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100 : 15)
            : gatewayOptions.GraphQL.MaxAllowedExecutionDepth;
        var maxCost = gatewayOptions.AreQueryLimitsRelaxed
            ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100000 : 5000)
            : gatewayOptions.GraphQL.MaxAllowedComplexity;

        // SEC M-16: Singleton, damit registrierte API-Keys und der Key-Cache über Requests hinweg bestehen.
        services.AddSingleton<IClientTierResolver, ClientTierResolver>();
        services.AddHttpClient<CloudflareCdnPurgeService>().AddSecureOutboundHandlers(EgressIntegrations.Cdn);
        services.AddHttpClient<FastlyCdnPurgeService>().AddSecureOutboundHandlers(EgressIntegrations.Cdn);
        services.AddHttpClient("OpenApiIngestion").AddSecureOutboundHandlers(EgressIntegrations.Catalog);
        services.AddTransient<ICdnCachePurgeService, CloudflareCdnPurgeService>();

        services.AddFusionFederationServices(gatewayOptions);

        services.AddSingleton<ErrorSanitizingFilter>();
        // R-GQL-12: one module instance (and change-detection timer) for the application, also across schema rebuilds.
        services.AddSingleton(sp => new CatalogGraphQlTypeModule(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<ITableRelationRepository>(),
            sp.GetService<ILogger<CatalogGraphQlTypeModule>>(),
            TimeSpan.FromSeconds(gatewayOptions.GraphQL.CatalogSchemaRefreshSeconds),
            aggregationEnabled: gatewayOptions.GraphQL.Aggregation.Enabled,
            crossSourceEnabled: gatewayOptions.IsCrossSourceEnabled,
            metricsEnabled: (gatewayOptions.GraphQL.Metrics?.Enabled ?? true) && gatewayOptions.GraphQL.Aggregation.Enabled,
            graphQlOptions: gatewayOptions.GraphQL));
        services.AddSingleton<ICatalogSchemaDiagnostics>(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>());
        services.AddSingleton<ISocketTokenValidator, JwtSocketTokenValidator>();
        services.AddOptions<RevocationWatchOptions>()
            .BindConfiguration(RevocationWatchOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<RevocationWatchOptions>, RevocationWatchOptionsValidator>();
        services.AddSingleton<IRealmSchemeSelector, RealmSchemeSelector>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, RequireFunctionalRightHandler>();
        services.AddOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>()
            .BindConfiguration(Autheris.Domain.Options.Security.IdentityRealmsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>, Autheris.Application.Identity.Validators.IdentityRealmOptionsValidator>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Autheris.Domain.Options.Security.IdentityRealmsOptions>, Autheris.Application.Identity.Validators.IdentityRealmsScimOptionsValidator>();
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
            .UseRequest<Autheris.GraphQL.Interceptors.CdnCacheTagMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.MaskedFieldsMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.PaginationTruncationMiddleware>()
            .TryAddTypeInterceptor<Autheris.GraphQL.Federation.SubgraphFieldMaskingTypeInterceptor>()
            .UseRequest<Autheris.GraphQL.Catalog.CatalogOperationCleanupMiddleware>()
            .UseOperationCache()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseRequest<Autheris.GraphQL.Interceptors.CostAndQuotaMiddleware>()
            .UseOperationExecution()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddApplicationService<WebSocketAuthInterceptor>()
            .AddApplicationService<ITableMetadataRepository>()
            .AddApplicationService<ITableRelationRepository>()
            .AddApplicationService<CatalogGraphQlTypeModule>()
            .AddApplicationService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()
            .AddTypeModule(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>())
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddGatewaySubscriptions(gatewayOptions.GraphQL)
            .AddDirectiveType<Autheris.GraphQL.Directives.McpToolDirectiveType>()
            .AddDirectiveType<Autheris.GraphQL.Directives.RebacDirectiveType>()
            .AddMaxExecutionDepthRule(maxDepth)
            .AddValidationRule<Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule>((sp, _) =>
                new Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule(
                    maxAllowedCost: maxCost,
                    maxResponseRows: gatewayOptions.GraphQL.MaxResponseRows,
                    onQueryTooComplex: () => GatewayDiagnostics.QueryTooComplexCounter.Add(1),
                    maxRootFields: gatewayOptions.AreQueryLimitsRelaxed
                        ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 200 : 25)
                        : gatewayOptions.GraphQL.MaxRootFieldsPerOperation,
                    maxExecutionDepth: Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule.ResolveMaxExecutionDepth(gatewayOptions.GraphQL),
                    aggregateFieldCostWeight: sp.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value?.Aggregation.AggregateFieldCostWeight
                        ?? Autheris.Domain.Options.AggregationOptions.DefaultAggregateFieldCostWeight,
                    maxGroupByColumns: sp.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value?.Aggregation.MaxGroupByColumns
                        ?? Autheris.Domain.Options.AggregationOptions.DefaultMaxGroupByColumns,
                    maxAggregates: sp.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.AnalyticsOptions>>()?.Value?.Aggregation.MaxAggregates
                        ?? Autheris.Domain.Options.AggregationOptions.DefaultMaxAggregates,
                    maxAggregateFieldsPerOperation: gatewayOptions.GraphQL.Aggregation.MaxFieldsPerOperation))
            // HotChocolate 16 adds the types __SchemaDefinition/__SearchResult (semantic introspection) to the schema.
            // Their names start with "__", which GraphQL reserves for introspection; graphql-js based clients (GraphiQL,
            // Apollo, codegen) reject the whole schema because of them. Autheris does not use the semantic search.
            .ModifyOptions(opt => opt.EnableSemanticIntrospection = false)
            .ModifyRequestOptions(opt =>
            {
                opt.ExecutionTimeout = TimeSpan.FromSeconds(gatewayOptions.HighAvailability.QueryTimeoutSeconds);
            });

        // SEC H-02: OpenSchema only opens catalog/OpenAPI documentation routes; it no longer enables introspection.
        if (!gatewayOptions.GraphQL.EnableIntrospection && !gatewayOptions.IsIntrospectionForced)
        {
            gqlBuilder.DisableIntrospection();

            // R-GQL-6: GET /graphql?sdl and /graphql/schema.graphql serve the SDL independently of introspection.
            gqlBuilder.ModifyServerOptions(opt => opt.EnableSchemaRequests = false);
        }

        return services;
    }
}
