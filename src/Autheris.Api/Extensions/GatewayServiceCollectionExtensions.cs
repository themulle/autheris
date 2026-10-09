using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ComponentModel.DataAnnotations;
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
using Autheris.Infrastructure.Diagnostics;
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

namespace Autheris.Api.Extensions;

public static class GatewayServiceCollectionExtensions
{
    public static GatewayOptions AddGatewayOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(opts =>
                opts.Audit.Retention.SecurityDays >= 365,
                "Security violation: Audit:Retention:SecurityDays must be at least 365 days.")
            .Validate(opts =>
                opts.Audit.AuditLogRetentionDays >= 365,
                "Security violation: Audit:AuditLogRetentionDays must be at least 365 days.")
            .Validate(opts =>
                Enum.IsDefined(opts.RowFilters.SubqueryStrategy),
                "Gateway:RowFilters:SubqueryStrategy must be Exists, InCorrelated or In.")
            .Validate(opts =>
                !opts.Casbin.Enabled || string.IsNullOrWhiteSpace(opts.Casbin.ModelPath) || System.IO.File.Exists(System.IO.Path.GetFullPath(opts.Casbin.ModelPath)),
                "Gateway:Casbin is enabled, but the configured ModelPath file was not found.")
            .Validate(opts =>
                !opts.Casbin.Enabled || !string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath),
                "Gateway:Casbin is enabled, but PolicyPath is not configured. Failing closed.")
            .Validate(opts =>
                !opts.Casbin.Enabled || string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath) || System.IO.File.Exists(System.IO.Path.GetFullPath(opts.Casbin.PolicyPath)),
                "Gateway:Casbin is enabled, but the configured PolicyPath file was not found.")
            .Validate(opts =>
                opts.HighAvailability.ShutdownTimeoutSeconds >= opts.HighAvailability.QueryTimeoutSeconds + 10,
                "NF-HA-01 violation: ShutdownTimeoutSeconds must be at least 10s greater than QueryTimeoutSeconds.")
            .Validate(opts =>
                opts.HighAvailability.TerminationGracePeriodSeconds >= opts.HighAvailability.DrainDelaySeconds + opts.HighAvailability.ShutdownTimeoutSeconds + 10,
                "NF-HA-01 violation: TerminationGracePeriodSeconds must be greater than DrainDelay + ShutdownTimeout + 10s.")
            .Validate(opts =>
                !(opts.Authentication.RequireKerberosOnly && opts.Authentication.BasicAuth.Enabled),
                "Security conflict: BasicAuth must not be enabled when RequireKerberosOnly is set to true.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.ForwardAuth.Enabled ||
                (!string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecret) || !string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecretKeyVaultRef)),
                "Security violation: Outside Development, ForwardAuth requires a configured SharedSecret or SharedSecretKeyVaultRef.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.ForwardAuth.Enabled || opts.Authentication.ForwardAuth.RequireTrustedProxy,
                "Security violation: RequireTrustedProxy must not be set to false when ForwardAuth is enabled outside Development.")
            .Validate(opts =>
#if AUTHERIS_TEST_AUTH
                environment.IsDevelopment() || !opts.Authentication.EnableTestAuthHandler,
                "Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.")
#else
                !opts.Authentication.EnableTestAuthHandler,
                "Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.")
#endif
            .Validate(opts =>
                environment.IsDevelopment() || !opts.IsAnonymousAccessAllowed,
                "Security violation: danger_allow_anonymous_access may be true ONLY in the Development environment.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.AreUntrustedCertificatesAllowed,
                "Security violation: danger_allow_untrusted_certificates may be true ONLY in the Development environment.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.GraphQL.TrustedOrigins.Contains("*"),
                "Security violation: TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection).")
            .Validate(opts =>
                environment.IsDevelopment() || opts.GraphQL.TrustedOrigins.All(o => o == "*" || (Uri.TryCreate(o, UriKind.Absolute, out var u) && string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))),
                "Security violation: Outside Development, TrustedOrigins must contain only HTTPS URLs.")
            .Validate(opts =>
                environment.IsDevelopment() || (
                    !string.IsNullOrWhiteSpace(opts.DataMasking.HmacSecretKeyVaultRef) &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "DEV_INSECURE_TEST_KEY_ONLY" &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "dev-only-hmac-salt-secure-fallback"
                ) || opts.IsInsecureTransportAllowed || opts.IsColumnMaskingDisabled,
                "NF-SEC-03 violation: Outside Development, HmacSecretKeyVaultRef must be a valid Key Vault secret reference.")
            .Validate(opts =>
                IsSupportedGovernanceDbProvider(opts.GovernanceDb.Provider),
                "The GovernanceDb provider currently supports only 'Sqlite', 'PostgreSql' or 'SqlServer'.")
            .Validate(opts =>
                !(opts.HighAvailability.MultiNodeClusterMode || opts.HighAvailability.Replicas > 1) ||
                !DataSourceProvider.Is(opts.GovernanceDb.Provider, DatabaseDialect.Sqlite),
                "Security violation (E-2): Multi-node cluster mode and more than 1 replica are not allowed with SQLite, because SQLite uses local database files per instance. Configure GovernanceDb.Provider = 'PostgreSql' or 'SqlServer' for cluster operation.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.OpenMetadata.Enabled ||
                (Uri.TryCreate(opts.OpenMetadata.ServerUrl, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ||
                opts.IsInsecureTransportAllowed,
                "Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.")
            .Validate(opts =>
                environment.IsDevelopment() || !(opts.HighAvailability.MultiNodeClusterMode || opts.HighAvailability.Replicas > 1) || opts.Caching.Redis.Enabled,
                "NF-HA-02 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide cache and epoch invalidation requires Caching.Redis.Enabled = true.")
            .Validate(opts =>
                environment.IsDevelopment() ||
                string.IsNullOrWhiteSpace(opts.Plugins.Directory) ||
                !Directory.Exists(System.IO.Path.GetFullPath(opts.Plugins.Directory)) ||
                opts.Plugins.RequireIntegrityManifest,
                "Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.")
            .Validate(opts =>
                environment.IsDevelopment() || opts.Rebac.SeedTuples.Count == 0 ||
                !opts.Rebac.SeedTuples.Any(t => t.User.Contains("david", StringComparison.OrdinalIgnoreCase) || t.Object.Contains("prod", StringComparison.OrdinalIgnoreCase)),
                "Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.")
            .ValidateOnStart();

        var gatewayOptions = configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        ValidateGatewayOptions(gatewayOptions, environment);

        services.Configure<HostOptions>(o =>
        {
            var drainBuffer = gatewayOptions.HighAvailability.DrainDelaySeconds + gatewayOptions.HighAvailability.ShutdownTimeoutSeconds + 5;
            o.ShutdownTimeout = TimeSpan.FromSeconds(Math.Min(drainBuffer, gatewayOptions.HighAvailability.TerminationGracePeriodSeconds));
        });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            if (gatewayOptions.ReverseProxy.Enabled)
            {
                // Preserve safe loopback defaults against spoofing
                options.KnownProxies.Add(System.Net.IPAddress.Loopback);
                options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);

                foreach (var netStr in gatewayOptions.ReverseProxy.KnownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(netStr, out var network))
                    {
                        options.KnownIPNetworks.Add(network);
                    }
                }

                foreach (var proxyStr in gatewayOptions.ReverseProxy.KnownProxies)
                {
                    if (System.Net.IPAddress.TryParse(proxyStr, out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }
            }
            else
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });

        return gatewayOptions;
    }

    public static IServiceCollection AddGatewayInfrastructure(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        var hostEnv = environment ?? (services.FirstOrDefault(d => d.ServiceType == typeof(IHostEnvironment))?.ImplementationInstance as IHostEnvironment);
        var isDev = hostEnv?.IsDevelopment() ?? false;

        if (!isDev && DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            var loggerFactory = services.FirstOrDefault(d => d.ServiceType == typeof(ILoggerFactory))?.ImplementationInstance as ILoggerFactory;
            var startupLogger = loggerFactory?.CreateLogger("Autheris.Startup");
            startupLogger?.LogWarning("AR-08: SQLite governance provider is not recommended for production environments. Consider PostgreSQL or SQL Server.");
        }

        services.AddMemoryCache(options =>
        {
            options.SizeLimit = (long)gatewayOptions.Caching.L1MemoryCache.SizeLimitMb * 1024 * 1024;
        });

        services.AddSingleton<IBinaryCacheSerializer, MemoryPackCacheSerializer>();

        if (gatewayOptions.Caching.Garnet.EnableEmbeddedServer)
        {
            var garnetManager = new GarnetServerManager(Microsoft.Extensions.Options.Options.Create(gatewayOptions));
            garnetManager.StartServer();
            services.AddSingleton<IGarnetServerManager>(garnetManager);
            services.AddHostedService(sp => (GarnetServerManager)sp.GetRequiredService<IGarnetServerManager>());

            var garnetConfig = new ConfigurationOptions
            {
                EndPoints = { $"{gatewayOptions.Caching.Garnet.Host}:{gatewayOptions.Caching.Garnet.Port}" },
                Password = garnetManager.ClientPassword, // SEC H-01: Garnet runs with --auth Password
                ConnectTimeout = gatewayOptions.Caching.Redis.ConnectTimeoutMs,
                SyncTimeout = gatewayOptions.Caching.Redis.SyncTimeoutMs,
                AbortOnConnectFail = false
            };
            RedisConnectionSecurity.ApplyGarnetClientTls(garnetConfig, gatewayOptions.Caching.Garnet); // SEC H-01: optional TLS
            services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(garnetConfig));
            services.AddSingleton<IEventBus, RedisEventBus>();
            services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
            services.AddSingleton<ITokenRevocationService, RedisTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddSingleton<Autheris.Application.State.IDistributedClusterStateProvider, Autheris.Infrastructure.State.RedisClusterStateProvider>();
        }
        else if (gatewayOptions.Caching.Redis.Enabled)
        {
            var redisConfig = ConfigurationOptions.Parse(gatewayOptions.Caching.Redis.Configuration);
            redisConfig.ConnectTimeout = gatewayOptions.Caching.Redis.ConnectTimeoutMs;
            redisConfig.SyncTimeout = gatewayOptions.Caching.Redis.SyncTimeoutMs;
            redisConfig.AbortOnConnectFail = false;
            services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(
                RedisConnectionSecurity.Apply(redisConfig, gatewayOptions.Caching.Redis, sp.GetService<IKeyVaultSecretProvider>(), sp.GetService<IHostEnvironment>())));
            services.AddSingleton<IEventBus, RedisEventBus>();
            services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
            services.AddSingleton<ITokenRevocationService, RedisTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddSingleton<Autheris.Application.State.IDistributedClusterStateProvider, Autheris.Infrastructure.State.RedisClusterStateProvider>();
        }
        else
        {
            services.AddSingleton<IEventBus, InProcessChannelEventBus>();
            services.AddSingleton<IRateLimiterService, InMemoryRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
            services.AddSingleton<ITokenRevocationService, InMemoryTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddSingleton<Autheris.Application.State.IDistributedClusterStateProvider, Autheris.Application.State.InMemoryClusterStateProvider>();
        }

        services.AddSingleton<ITableSensitivityLookup>(sp => new TableMetadataSensitivityLookup(
            () => sp.GetService<ITableMetadataRepository>(),
            sp.GetService<ILogger<TableMetadataSensitivityLookup>>()));
        services.AddSingleton<IEpochValidationService, EpochValidationService>();
        services.AddSingleton<Autheris.Application.VirtualFilters.VirtualFilterAdministrationService>();
        services.AddSingleton<Autheris.Application.VirtualFilters.IVirtualFilterSnapshotProvider, Autheris.Application.VirtualFilters.VirtualFilterSnapshotProvider>();
        services.AddSingleton<Autheris.Application.VirtualFilters.IVirtualFilterPredicateBuilder, Autheris.Application.VirtualFilters.StructuredFilterSqlBuilder>();
        services.AddSingleton<Autheris.Application.VirtualFilters.MandatoryRowFilterResolver>();
        services.AddSingleton<Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver>(sp => sp.GetRequiredService<Autheris.Application.VirtualFilters.MandatoryRowFilterResolver>());
        services.AddSingleton<IConsentCacheService, ConsentCacheService>();
        services.AddSingleton<IParameterBudgetProvider, DatabaseParameterBudgetProvider>();
        if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.PostgreSql))
        {
            services.AddSingleton<PostgreSqlGovernanceRepository>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<Autheris.Application.VirtualFilters.IVirtualFilterRepository>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository, InMemoryAccessProfileRepository>();
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<PostgreSqlGovernanceRepository>());
        }
        else if (DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.SqlServer))
        {
            services.AddSingleton<SqlServerGovernanceRepository>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<Autheris.Application.VirtualFilters.IVirtualFilterRepository>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository, InMemoryAccessProfileRepository>();
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<SqlServerGovernanceRepository>());
        }
        else
        {
            services.AddSingleton<SqliteGovernanceRepository>();
            services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<Autheris.Application.VirtualFilters.IVirtualFilterRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAccessProfileRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
            services.AddSingleton<IAuditChainExportSource>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        }
        services.AddSingleton<Autheris.Application.Policy.Interfaces.IAccessProfileCache, Autheris.Application.Policy.Services.AccessProfileCache>();
        services.AddSingleton<IDbtProposalRepository, InMemoryDbtProposalRepository>();
        services.AddSingleton<IDbtHealthCircuitBreaker, DbtHealthCircuitBreaker>();
        services.AddSingleton<IOpenApiCacheManager, OpenApiCacheManager>();
        services.AddSingleton(new Autheris.Domain.Model.OpenApiDocumentOptions { IncludeStoredProcedureSpec = gatewayOptions.SqlEndpoints.Procedures.Enabled, IncludeWebSqlSpec = gatewayOptions.WebSql.Enabled });
        services.AddSingleton<IDynamicOpenApiGenerator, DynamicOpenApiGenerator>();
        services.AddSingleton<IRlsFilterGenerator, RlsFilterGenerator>();
        services.AddSingleton<IRowFilterSqlBuilder, RowFilterSqlBuilder>();
        services.AddSingleton<IConsentResolutionService, ConsentResolutionService>();
        services.AddSingleton<IIdentitySubjectResolver, IdentitySubjectResolver>();
        services.AddSingleton<IKeyVaultSecretProvider, DefaultEnvironmentSecretProvider>();
        services.AddSingleton<IColumnMaskingProvider, ColumnMaskingProvider>();
        services.AddSingleton<IChunkedQueryExecutor>(sp => new ChunkedQueryExecutor(
            gatewayOptions.GraphQL.MaxInClauseBatchSize,
            sp.GetRequiredService<IParameterBudgetProvider>()));

        // Outbound SSRF protection (HIGH-03 / SEC-02) & OpenAPI ingestion (P1).
        // Data catalog clients, factory and sync are registered by AddGatewayExtensions (Autheris.Extensions/DataCatalog).
        // SEC E-03: hardened primary handler (no redirects, connect-time IP check); allowlist only if "AuditWorm" is listed
        // in Egress.TrustedIntegrations (SEC E-02).
        services.AddHttpClient<IAuditWormExportService, AuditWormExportService>().AddSecureOutboundHandlers(EgressIntegrations.AuditWorm);
        services.AddSingleton<IOpenApiIngestionService, OpenApiIngestionService>();

        // SQL Connection Factory & Health Checks
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<Autheris.Infrastructure.Health.AuditChainIntegrityMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Autheris.Infrastructure.Health.AuditChainIntegrityMonitor>());
        // SEC R3-4: anonymous /health/ready must not drive the DB probe per request -> cached, single-flight decorator.
        services.AddSingleton<GatewayHealthCheckService>();
        services.AddSingleton<IGatewayHealthCheckService>(sp =>
            new Autheris.Infrastructure.Health.CachedGatewayHealthCheckService(sp.GetRequiredService<GatewayHealthCheckService>()));

        // HTTP & Plugin Data Sources
#pragma warning disable CA5359 // Intentionally allowed via danger_allow_untrusted_certificates for Getting Started / Dev
        if (gatewayOptions.AreUntrustedCertificatesAllowed)
        {
            services.ConfigureHttpClientDefaults(builder =>
            {
                builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
            });
        }

        services.AddHttpClient(DeclarativeHttpDataSourceExecutor.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => SecureOutboundHttp.CreatePrimaryHandler(sp, "DeclarativeHttp"))
            // SEC I-1: same URL/address decision as every other integration client (also used by plugins).
            .AddHttpMessageHandler(sp => SsrfProtectionHandler.Create(sp, "DeclarativeHttp"));
#pragma warning restore CA5359
        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<IDataSourceExecutor, SqlDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, DeclarativeHttpDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, PluginHttpDataSourceExecutor>();
        services.AddSingleton<TrinoSqlEngine.ISqlEngine, TrinoSqlEngine.FastSqlEngine>();
        services.AddSingleton<Autheris.Application.Sql.Interfaces.ISqlSecurityValidator, Autheris.Application.Sql.Services.DefaultSqlSecurityValidator>();
        services.AddScoped<Autheris.Application.Sql.Interfaces.IGovernedSqlExecutionService, Autheris.Application.Sql.Services.GovernedSqlExecutionService>();
        services.AddSingleton<Autheris.Application.Sql.Interfaces.IWebSqlStatementManager, Autheris.Application.Sql.Services.WebSqlStatementManager>();
        services.AddSingleton<Autheris.Application.SqlEndpoints.Interfaces.ISqlEndpointRegistry, Autheris.Application.SqlEndpoints.Services.InMemorySqlEndpointRegistry>();
        services.AddSingleton<Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader>();
        services.AddScoped<Autheris.Application.SqlEndpoints.Interfaces.ISqlEndpointExecutionService, Autheris.Application.SqlEndpoints.Services.SqlEndpointExecutionService>();

        // F-SQL-02: governed stored procedure endpoints (SQL Server, read-only in phase 1)
        services.AddSingleton<Autheris.Application.Procedures.Interfaces.IProcedureRegistry, Autheris.Application.Procedures.Services.InMemoryProcedureRegistry>();
        services.AddSingleton<Autheris.Application.Procedures.Services.ProcedureDefinitionLoader>();
        services.AddSingleton<Autheris.Application.Procedures.Services.ProcedureConnectionProvider>();
        services.AddScoped<Autheris.Application.Procedures.Services.StoredProcedureCatalogValidator>();
        services.AddSingleton<Autheris.Application.Procedures.Interfaces.IProcedureInvoker, Autheris.Application.Procedures.Services.MssqlProcedureInvoker>();
        services.AddSingleton<Autheris.Application.Procedures.Interfaces.IProcedureRowScopeResolver, Autheris.Application.Procedures.Services.SqlProcedureRowScopeResolver>();
        services.AddScoped<Autheris.Application.Procedures.Interfaces.IProcedureExecutionService, Autheris.Application.Procedures.Services.GovernedProcedureExecutionService>();
        services.AddSingleton<Autheris.Application.Procedures.Tools.IProcedureYamlGenerator, Autheris.Application.Procedures.Tools.ProcedureYamlGenerator>();
        if (gatewayOptions.SqlEndpoints.Procedures.Enabled)
        {
            services.AddHostedService<Autheris.Application.Procedures.Services.ProcedureRegistrationService>();
        }

        // Casbin ABAC Engine
        services.AddSingleton<IPolicyEnforcementService>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            var rlsGen = sp.GetService<Autheris.Application.Interfaces.IRlsFilterGenerator>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<CasbinEnforcementService>>();
            if (!options.Casbin.Enabled)
            {
                logger?.LogWarning("Casbin ABAC engine is DISABLED (Gateway:Casbin:Enabled = false). Access control via Casbin policies is inactive.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(options.Casbin.ModelPath))
                {
                    var fullModelPath = System.IO.Path.GetFullPath(options.Casbin.ModelPath);
                    if (!System.IO.File.Exists(fullModelPath))
                    {
                        throw new FileNotFoundException($"Gateway:Casbin is enabled, but model file '{fullModelPath}' was not found. Failing closed.");
                    }
                }
                if (string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
                {
                    throw new InvalidOperationException("Gateway:Casbin is enabled, but PolicyPath is not configured. Failing closed.");
                }

                var fullPolicyPath = System.IO.Path.GetFullPath(options.Casbin.PolicyPath);
                if (!System.IO.File.Exists(fullPolicyPath))
                {
                    throw new FileNotFoundException($"Gateway:Casbin is enabled, but policy file '{fullPolicyPath}' was not found. Failing closed.");
                }
            }

            var service = new CasbinEnforcementService(
                string.IsNullOrWhiteSpace(options.Casbin.ModelPath) ? null : options.Casbin.ModelPath,
                rlsGen,
                logger);
            if (options.Casbin.Enabled && !string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
            {
                service.LoadPolicyFromFile(options.Casbin.PolicyPath, options.Casbin.WatchPolicyFile);
            }
            return service;
        });

        // Strategic Enterprise Moats (P10, P11, P12)
        services.AddSingleton<IPolicySimulationService, PolicySimulationService>();
        services.AddSingleton<ISchemaSunsettingService, SchemaSunsettingService>();
        services.AddSingleton<IDifferentialPrivacyEngine, DifferentialPrivacyEngine>();
        services.AddSingleton<IEuAiActAuditExporter, EuAiActAuditExporter>();

        // ITSM orchestration (dispatcher, recertification, outbox workers). The outbound REST clients (ServiceNow & Jira)
        // and the inbound webhook handler are registered by AddGatewayExtensions (Autheris.Extensions/Itsm).
        services.AddScoped<ItsmWorkflowDispatcher>();
        services.AddScoped<IConsentRecertificationService, ConsentRecertificationWorkflowService>();
        if (gatewayOptions.Itsm.Enabled)
        {
            services.AddHostedService<ItsmOutboxDispatcherHostedService>();
            services.AddHostedService<ConsentRecertificationHostedService>();
        }


        // Lineage Graph Store, Impact Analyzer & GDPR Exporter. The external OpenLineage export client is registered by
        // AddGatewayExtensions (Autheris.Extensions/Lineage).
        services.AddSingleton<ILineageGraphStore, LineageGraphStore>();
        services.AddScoped<ILineageImpactAnalyzerService, LineageImpactAnalyzerService>();
        services.AddSingleton<IGdprAuditReportExporter, GdprAuditReportPdfExporter>();

        // AI Assisted Governance (Triage). The OpenJEV client is registered by AddGatewayExtensions (Autheris.Extensions/Lineage).
        services.AddScoped<IJustificationTriageService, JustificationTriageService>();

        // Standardisiertes Connector-SPI (F-ARCH-10 nach Trino-Muster)
        services.AddSingleton<Autheris.Application.Connectors.IAutherisConnectorRegistry>(sp =>
        {
            var registry = new Autheris.Infrastructure.Connectors.InMemoryConnectorRegistry();
            var sqlConnFactory = sp.GetService<ISqlConnectionFactory>();
            var metaRepo = sp.GetService<ITableMetadataRepository>();
            var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>();
            var env = sp.GetService<IHostEnvironment>();
            var maskingProvider = sp.GetService<IColumnMaskingProvider>();

            if (sqlConnFactory != null && metaRepo != null)
            {
                var defaultSqlConnector = new Autheris.Infrastructure.Connectors.SqlConnector(
                    connectorId: "default-sql",
                    connectionFactory: sqlConnFactory,
                    metadataRepository: metaRepo,
                    options: opts,
                    environment: env,
                    maskingProvider: maskingProvider);
                registry.RegisterConnector("default-sql", defaultSqlConnector);
                registry.RegisterConnector("sql", defaultSqlConnector);
            }
            return registry;
        });

        services.AddScoped<Autheris.Application.Connectors.CrossDomain.ICrossDomainAccessResolver, Autheris.Application.Connectors.CrossDomain.DefaultCrossDomainAccessResolver>();

        services.AddScoped<IClientIpResolver, Autheris.Api.Security.HttpContextClientIpResolver>();
        services.AddScoped<Autheris.Domain.Audit.AuditContext>();
        services.AddSingleton<Autheris.Application.Audit.IAuthFailureAggregator, Autheris.Application.Audit.AuthFailureAggregator>();
        // O10: process-wide counter of running table reads per tenant, user and table
        services.AddSingleton<ITableReadConcurrencyGate, TableReadConcurrencyGate>();
        // M-1: database session context initializer for PostgreSQL GUCs and SQL Server SESSION_CONTEXT
        services.AddSingleton<IDbSessionContextInitializer, DbSessionContextInitializer>();
        services.AddScoped<GatewayExecutionService>(sp => new GatewayExecutionService(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetRequiredService<IAuditLogRepository>(),
            sp.GetRequiredService<IConsentResolutionService>(),
            sp.GetRequiredService<IConsentCacheService>(),
            sp.GetRequiredService<IColumnMaskingProvider>(),
            sp.GetRequiredService<IChunkedQueryExecutor>(),
            sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>(),
            sp.GetService<ITrafficDrainController>(),
            sp.GetServices<IDataSourceExecutor>(),
            sp.GetService<IPolicyEnforcementService>(),
            sp.GetService<IClientIpResolver>(),
            sp.GetService<Autheris.Application.Connectors.IAutherisConnectorRegistry>(),
            sp.GetService<ITableReadConcurrencyGate>(),
            sp.GetService<Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator>(),
            sp.GetRequiredService<Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver>()));
        services.AddScoped<IGatewayExecutionService>(sp => sp.GetRequiredService<GatewayExecutionService>());
        services.AddScoped<ITableAccessResolver>(sp => sp.GetRequiredService<GatewayExecutionService>());
        services.AddScoped<IGovernedTreeQueryService, GovernedTreeQueryService>();
        services.AddScoped<IUnifiedPolicyDecisionPoint, UnifiedPolicyDecisionPoint>();

        // Model Context Protocol (MCP) Server & AI Data Guardrails
        services.AddSingleton<ISemanticPromptGuardrail, SemanticPromptGuardrail>();
        services.AddSingleton<IGoldenQueryService, GoldenQueryService>();
        services.AddSingleton<ISemanticMcpCompiler, SemanticMcpCompiler>();
        services.AddScoped<IMcpDatasetCatalog, McpDatasetCatalog>();
        services.AddSingleton<IGraphQlCatalogMap, Autheris.GraphQL.Mcp.CatalogGraphQlMap>();
        services.AddTransient<IPreFlightQuerySimulator, PreFlightQuerySimulator>();
        services.AddSingleton<IMcpProvenanceEnricher, McpProvenanceEnricher>();
        services.AddSingleton<IMcpSessionStore, McpSessionStore>();
        services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        services.AddSingleton<Autheris.Application.Mcp.Pruning.ISemanticToolPruner, Autheris.Application.Mcp.Pruning.SemanticToolPruner>();
        services.AddSingleton<IPersistedToolValidator, PersistedToolValidator>();
        services.AddScoped<IMcpQueryExecutor, Autheris.GraphQL.Mcp.GatewayMcpQueryExecutor>();
        Autheris.Api.Mcp.GatewayMcpServer.AddGatewayMcpServer(services, gatewayOptions);
        services.AddScoped<IAiDataGuardrailService, AiDataGuardrailService>();
        services.AddScoped<IMcpProtocolHandler, McpProtocolHandler>();
        services.AddScoped<IMcpStdioRunner, McpStdioRunner>();
        services.AddHostedService<Autheris.GraphQL.Mcp.McpSchemaDiscoveryService>();

        // Resource Groups & Workload Isolation (F-PERF-08)
        services.AddSingleton<IResourceGroupManager, ResourceGroupManager>();
        // SEC H-07: Per-principal / per-tenant limits for long-lived connections (WebSocket, SSE)
        services.AddSingleton(sp => new PersistentConnectionLimiter(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.ResourceGroups));

        // Canonical System Metadata & Monitoring (F-API-07)
        services.AddSingleton<IGatewaySystemMetricsService, GatewaySystemMetricsService>();

        // Plan Cache (F-PERF-09 / graphql-bench / AR-10 / AR-19)
        services.AddSingleton<ICompiledSqlQueryPlanCache>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            var planCacheOptions = options.WebSql.PlanCache;
            if (planCacheOptions.MaxEntries <= 0)
            {
                return NullCompiledSqlQueryPlanCache.Instance;
            }

            var ttl = planCacheOptions.TtlSeconds > 0
                ? TimeSpan.FromSeconds(planCacheOptions.TtlSeconds)
                : TimeSpan.FromMinutes(10);

            return new CompiledSqlQueryPlanCache(planCacheOptions.MaxEntries, ttl);
        });

        // Human-in-the-Loop Step-Up Approval (F-AI-05)
        services.AddSingleton<IHitLStepUpApprovalService, HitLStepUpApprovalService>();

        // Hierarchical Parquet Egress (F-DATA-01)
        services.AddSingleton<IParquetExportService, ParquetExportService>();

        // Native Apache Arrow Flight SQL & IPC Egress (F-DATA-04 & F-DATA-04-B)
        services.AddSingleton<Autheris.Application.Serialization.IArrowExportService, Autheris.Application.Serialization.ArrowExportService>();
        services.AddSingleton<Autheris.Application.Serialization.IArrowFlightSqlServer, Autheris.Application.Serialization.ArrowFlightSqlServer>();

        // Embedded In-Memory OLAP via DuckDB.NET (F-DATA-03)
        services.AddSingleton<Autheris.Application.Olap.IDuckDbOlapEngine>(sp =>
            new Autheris.Application.Olap.DuckDbOlapEngine(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Olap.DuckDbOlapEngine>>()));


        // HA & Traffic Drain
        services.AddSingleton<ITrafficDrainController, TrafficDrainController>();
        services.AddHostedService<TrafficDrainHostedService>();

        // Connectors to foreign systems (Autheris.Extensions): ITSM (ServiceNow, Jira), OpenMetadata, data catalogs
        // (Purview, Collibra, OpenMetadata, Alation), dbt, OData, Iceberg lakehouse, OpenLineage/OpenJEV, Backstage and
        // CDC sources (MSSQL Change Tracking, Debezium). Single registration point – see ExtensionsServiceCollectionExtensions.
        services.AddGatewayExtensions(gatewayOptions);

        // Realtime Event Subscriptions & In-Stream RLS (P5 & F-CDC-03)
        services.AddSingleton<ICdcEventChannel, InMemoryCdcEventChannel>();
        services.AddSingleton<Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor>();
        services.AddSingleton<ICdcEventIngestionService, CdcEventIngestionService>();
        services.AddScoped<IStreamRlsPolicyEnforcer, StreamRlsPolicyEnforcer>();
        services.AddSingleton<Autheris.Infrastructure.Streaming.PostgreSqlLogicalReplicationService>();
        services.AddSingleton<Autheris.Application.Streaming.Interfaces.IPostgreSqlCdcService>(sp => sp.GetRequiredService<Autheris.Infrastructure.Streaming.PostgreSqlLogicalReplicationService>());
        if (gatewayOptions.PostgreSqlCdc.Enabled)
        {
            services.AddHostedService(sp => sp.GetRequiredService<Autheris.Infrastructure.Streaming.PostgreSqlLogicalReplicationService>());
        }

        // F-EVT-01: CloudEvents v1.0 Outbound Webhook Subscriptions
        services.AddSingleton<Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore, Autheris.Application.Events.Services.InMemoryCloudEventSubscriptionStore>();
        services.AddSingleton<Autheris.Application.Events.Interfaces.ICloudEventTransformer, Autheris.Application.Events.Services.CloudEventTransformer>();
        services.AddHttpClient<Autheris.Application.Events.Interfaces.ICloudEventWebhookDispatcher, Autheris.Application.Events.Services.CloudEventWebhookDispatcher>()
            .AddSecureOutboundHandlers("CloudEvents");

        // F-ARCH-11: Envoy External Authorization & Istio Service Mesh Adapter
        services.AddSingleton<Autheris.Application.Mesh.Interfaces.IEnvoyExtAuthzService, Autheris.Application.Mesh.Services.EnvoyExtAuthzService>();

        // AST-Aware Traffic Shadowing & Dark Replay (F-OPS-01, INF-2)
        services.AddHttpClient<Autheris.Application.Diagnostics.Shadowing.TrafficShadowingService>()
            .AddSecureOutboundHandlers(EgressIntegrations.Shadowing);
        services.AddSingleton<Autheris.Application.Diagnostics.Shadowing.TrafficShadowingService>();
        services.AddSingleton<Autheris.Application.Diagnostics.Shadowing.ITrafficShadowingService>(sp =>
            sp.GetRequiredService<Autheris.Application.Diagnostics.Shadowing.TrafficShadowingService>());
        if (gatewayOptions.TrafficShadowing.Enabled)
        {
            services.AddHostedService(sp => sp.GetRequiredService<Autheris.Application.Diagnostics.Shadowing.TrafficShadowingService>());
        }

        // FOCUS FinOps Accounting (F-AI-08)
        services.AddSingleton<Autheris.Application.FinOps.Interfaces.IFinOpsAccountingService, Autheris.Application.FinOps.Services.FocusCostAccountingService>();

        // Dynamic Schema Contracts (@tag / @inaccessible) (F-GOV-08)
        services.AddSingleton<Autheris.Application.Governance.Contracts.ISchemaContractManager, Autheris.Application.Governance.Contracts.SchemaContractManager>();

        // Incremental Delivery (@defer & @stream) (F-PERF-12)
        services.AddSingleton<Autheris.Application.Performance.IncrementalDelivery.IIncrementalDeliveryFormatter, Autheris.Application.Performance.IncrementalDelivery.IncrementalDeliveryFormatter>();
        services.AddSingleton<Autheris.Application.Performance.IncrementalDelivery.IIncrementalDeliveryManager, Autheris.Application.Performance.IncrementalDelivery.IncrementalDeliveryManager>();

        // ReBAC / OpenFGA Relationship-Based Access Control (F-SEC-04)
        // RR-L4-04: cluster-wide persistent tuple store whenever Redis/Garnet is configured; in-memory only for single-node.
        services.AddSingleton<Autheris.Application.Security.Rebac.Interfaces.IRebacStore>(sp =>
        {
            var multiplexer = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>();
            var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            Autheris.Application.Security.Rebac.Interfaces.IRebacStore store = multiplexer != null
                ? new Autheris.Infrastructure.Rebac.RedisRebacStore(
                    multiplexer,
                    opts,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Infrastructure.Rebac.RedisRebacStore>>())
                : new Autheris.Application.Security.Rebac.Services.InMemoryRebacStore(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Security.Rebac.Services.InMemoryRebacStore>>());

            var tuplesToSeed = new List<Autheris.Domain.Model.RebacTuple>(opts.Value.Rebac.SeedTuples);
            if (tuplesToSeed.Count == 0 && env?.IsDevelopment() == true)
            {
                tuplesToSeed.AddRange([
                    new("default", "user:david", "viewer", "table:lakehouse.dbo.orders"),
                    new("default", "S-1-5-21-LWE-DAVID", "viewer", "table:lakehouse.dbo.orders"),
                    new("default", "user:david", "viewer", "table:sales.public.orders"),
                    new("default", "S-1-5-21-LWE-DAVID", "viewer", "table:sales.public.orders"),
                    new("default", "user:david", "viewer", "table:sales.crm.contacts"),
                    new("tenant_lwe", "user:david", "viewer", "table:lakehouse.dbo.orders"),
                    new("tenant_lwe", "S-1-5-21-LWE-DAVID", "viewer", "table:lakehouse.dbo.orders")
                ]);
            }

            if (tuplesToSeed.Count > 0)
            {
                try
                {
                    store.AddTuplesAsync(tuplesToSeed).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                        ?.CreateLogger("RebacSeeder")
                        .LogWarning(ex, "Failed to preload ReBAC seed tuples.");
                }
            }

            return store;
        });
        services.AddSingleton<Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator, Autheris.Application.Security.Rebac.Services.ZanzibarRebacEvaluator>();
        services.AddScoped<Autheris.Application.Security.Rebac.Interfaces.IRebacBatchDataLoader, Autheris.Application.Security.Rebac.Services.RebacBatchDataLoader>();

        // Explicit CORS policy configuration
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var trusted = gatewayOptions.GraphQL.TrustedOrigins;
                if (trusted.Count > 0)
                {
                    if (trusted.Contains("*"))
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    }
                    else
                    {
                        policy.WithOrigins(trusted.Where(o => o != "*").ToArray())
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials();
                    }
                }
                else if (isDev)
                {
                    policy.WithOrigins("http://localhost:5000", "https://localhost:5001")
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                }
            });

            if (isDev && gatewayOptions.Mcp.EnableDeveloperCors)
            {
                options.AddPolicy("McpDeveloperCors", policy =>
                {
                    var extraOrigins = gatewayOptions.Mcp.DeveloperCorsOrigins != null
                        ? new HashSet<string>(gatewayOptions.Mcp.DeveloperCorsOrigins, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    policy.SetIsOriginAllowed(origin =>
                    {
                        if (string.IsNullOrWhiteSpace(origin))
                        {
                            return false;
                        }

                        if (extraOrigins.Contains(origin))
                        {
                            return true;
                        }

                        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                        {
                            if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                                (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) &&
                                uri.Port > 0)
                            {
                                return true;
                            }
                        }

                        return false;
                    })
                    .WithHeaders("Content-Type", "Authorization", "Mcp-Session-Id", "MCP-Protocol-Version", "Last-Event-ID")
                    .WithExposedHeaders("Mcp-Session-Id", "WWW-Authenticate")
                    .WithMethods("GET", "POST", "DELETE", "OPTIONS");
                });
            }
        });

        // OpenTelemetry Tracing & Metrics with OTLP Exporter
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.AddSource(GatewayDiagnostics.ActivitySourceName);
                tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(GatewayDiagnostics.MeterName);
                metrics.AddOtlpExporter();
            });

        // Extensibility Pipeline & Interceptors (P9)
        services.AddSingleton<IExtensibilityPipeline, ExtensibilityPipeline>();
        services.AddSingleton<IIngressInterceptor, JustificationAndBreakGlassInterceptor>();
        services.AddSingleton<IEgressInterceptor, AuditLineageEgressInterceptor>();

        // Schema Registry & AST Linter (P8)
        services.AddSingleton<FluentValidation.IValidator<SchemaRegistrationRequest>, SchemaRegistrationRequestValidator>();
        services.AddSingleton<ISchemaLinter, SchemaLinter>();
        services.AddSingleton<ISchemaRegistryRepository, InMemorySchemaRegistryRepository>();
        services.AddSingleton<ISchemaRegistryService, SchemaRegistryService>();

        return services;
    }

    public static IServiceCollection AddGatewayAuth(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment environment)
    {
        services.AddSingleton<ITrustedProxyValidator, TrustedProxyValidator>();
        services.AddTransient<IClaimsTransformation, EnterpriseClaimsTransformation>();

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = GatewayAuthSchemes.DefaultScheme;
            options.DefaultChallengeScheme = GatewayAuthSchemes.DefaultScheme;
        });

        // 1. Basic Authentication
        authBuilder.AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
            GatewayAuthSchemes.Basic, _ => { });

        // 1b. F-AUTH-DX: cookie session after a successful Basic login (allowlisted non-production environments only)
        var isBasicSessionAllowed = BasicAuthSession.IsAllowed(gatewayOptions, environment);
        var basicSessionCookieName = gatewayOptions.Authentication.BasicAuth.Session.CookieName;
        if (isBasicSessionAllowed)
        {
            var dataProtection = services.AddDataProtection().SetApplicationName("Autheris.Gateway");
            if (!string.IsNullOrWhiteSpace(gatewayOptions.Authentication.BasicAuth.Session.KeyDirectory))
            {
                dataProtection.PersistKeysToFileSystem(new System.IO.DirectoryInfo(gatewayOptions.Authentication.BasicAuth.Session.KeyDirectory));
            }

            services.AddSingleton<BasicAuthSessionCookieEvents>();
            authBuilder.AddCookie(BasicAuthSession.SchemeName, cookie =>
                BasicAuthSession.ConfigureCookie(cookie, gatewayOptions.Authentication.BasicAuth.Session));
            Console.WriteLine(
                "[Autheris] WARNING: BasicAuth.Session is active (developer login cookie). Never enable it in Production.");
        }
        else if (BasicAuthSession.GetInactiveReason(gatewayOptions, environment) is { } inactiveReason)
        {
            Console.WriteLine($"[Autheris] WARNING: BasicAuth.Session is configured but inactive: {inactiveReason}.");
        }

        // 2. Traefik / Kubernetes Ingress ForwardAuth
        authBuilder.AddScheme<AuthenticationSchemeOptions, ForwardAuthAuthenticationHandler>(
            GatewayAuthSchemes.ForwardAuth, _ => { });
        services.AddHostedService<ForwardAuthSecretStartupValidator>();

        // 3. Windows Negotiate (Kerberos / NTLM) or TestAuthHandler
#if AUTHERIS_TEST_AUTH
        bool isTestAuthAllowed = environment.IsDevelopment() &&
            (gatewayOptions.Authentication.EnableTestAuthHandler || gatewayOptions.IsAnonymousAccessAllowed);

        if (isTestAuthAllowed)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        }
        else
#endif
        {
            // Review E-2: never persist credentials on the (possibly shared, reverse-proxied) upstream connection, and
            // with RequireKerberosOnly reject every Negotiate identity that is not Kerberos (e.g. NTLM).
            authBuilder.AddNegotiate(NegotiateDefaults.AuthenticationScheme, negotiate =>
                NegotiateHardening.Configure(negotiate, gatewayOptions.Authentication.RequireKerberosOnly));
        }

        // 4. Microsoft Entra ID (Azure AD) and/or AD FS JWT Bearer
        var entraConfig = gatewayOptions.Authentication.EntraId;
        var adfsConfig = gatewayOptions.Authentication.Adfs;

        // API-14: with Entra ID and AD FS both enabled a single JwtBearer scheme could only load the signing keys of one
        // authority (Entra), so every AD FS token failed. Each IdP then gets its own scheme with its own metadata;
        // Bearer tokens are routed by their (unverified) issuer and fully validated by the selected scheme.
        bool splitJwtSchemes = entraConfig.Enabled && adfsConfig.Enabled;
        authBuilder.AddJwtBearer(GatewayAuthSchemes.JwtBearer, options => ConfigureJwtBearer(options, useEntra: entraConfig.Enabled, useAdfs: adfsConfig.Enabled && !splitJwtSchemes));
        // MCP OAuth discovery (RFC 9728 protected resource metadata) for the configured token issuers.
        Autheris.Api.Mcp.GatewayMcpOAuth.AddGatewayMcpOAuth(authBuilder, gatewayOptions);
        if (splitJwtSchemes)
        {
            authBuilder.AddJwtBearer(GatewayAuthSchemes.JwtBearerAdfs, options => ConfigureJwtBearer(options, useEntra: false, useAdfs: true));
        }

        void ConfigureJwtBearer(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options, bool useEntra, bool useAdfs)
        {
            // Review E-1: JwtBearer keeps the default inbound claim mapping (sub -> NameIdentifier, oid -> objectidentifier
            // URI); revocation lookups (GetLookupKeys) accept both spellings.
            // SEC SG-37: RequireHttpsMetadata must always be true outside Development.
            options.RequireHttpsMetadata = !environment.IsDevelopment() ||
                                           (useEntra && entraConfig.RequireHttpsMetadata) ||
                                           (useAdfs && adfsConfig.RequireHttpsMetadata);

            if (useEntra && !string.IsNullOrWhiteSpace(entraConfig.TenantId))
            {
                var instance = string.IsNullOrWhiteSpace(entraConfig.Instance)
                    ? "https://login.microsoftonline.com/"
                    : entraConfig.Instance.TrimEnd('/') + "/";
                options.Authority = $"{instance}{entraConfig.TenantId}/v2.0";
            }
            else if (useAdfs && !string.IsNullOrWhiteSpace(adfsConfig.Authority))
            {
                options.Authority = adfsConfig.Authority.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(adfsConfig.MetadataAddress))
                {
                    options.MetadataAddress = adfsConfig.MetadataAddress;
                }
            }

            var validIssuers = new List<string>();
            var validAudiences = new List<string>();
            var entraIssuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (useEntra)
            {
                if (!string.IsNullOrWhiteSpace(entraConfig.TenantId))
                {
                    var instance = string.IsNullOrWhiteSpace(entraConfig.Instance)
                        ? "https://login.microsoftonline.com/"
                        : entraConfig.Instance.TrimEnd('/') + "/";
                    entraIssuers.Add($"{instance}{entraConfig.TenantId}/v2.0");
                    entraIssuers.Add($"https://sts.windows.net/{entraConfig.TenantId}/");
                    validIssuers.AddRange(entraIssuers);
                }
                if (!string.IsNullOrWhiteSpace(entraConfig.Audience)) validAudiences.Add(entraConfig.Audience);
                if (!string.IsNullOrWhiteSpace(entraConfig.ClientId)) validAudiences.Add(entraConfig.ClientId);
            }

            if (useAdfs)
            {
                if (!string.IsNullOrWhiteSpace(adfsConfig.Authority))
                {
                    validIssuers.Add(adfsConfig.Authority.TrimEnd('/'));
                    validIssuers.Add($"{adfsConfig.Authority.TrimEnd('/')}/services/trust");
                }
                if (!string.IsNullOrWhiteSpace(adfsConfig.Audience)) validAudiences.Add(adfsConfig.Audience);
            }

            // SEC M-02: Issuer and audience are ALWAYS validated (fail-closed). If no issuer/audience is
            // configured, no token can pass validation; outside Development startup is aborted beforehand.
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = validIssuers.Count > 0 ? validIssuers : null,
                ValidateAudience = true,
                ValidAudiences = validAudiences.Count > 0 ? validAudiences : null,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            options.IncludeErrorDetails = false;

            if (useEntra)
            {
                // Finding 3.3 / R13: scope and token-kind rules apply to Entra tokens only (ADFS may share this scheme).
                options.Events ??= new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents();
                options.Events.OnTokenValidated = ctx =>
                {
                    if (ctx.Principal != null && entraIssuers.Contains(ctx.SecurityToken?.Issuer ?? string.Empty))
                    {
                        var resolver = ctx.HttpContext.RequestServices.GetRequiredService<IIdentitySubjectResolver>();
                        var failure = EntraTokenPolicy.Apply(ctx.Principal, entraConfig, resolver);
                        if (failure != null)
                        {
                            ctx.Fail(failure);
                        }
                    }

                    return Task.CompletedTask;
                };
            }
        }

        // 5. Smart Dynamic Policy Scheme: Route requests based on Authorization header or ForwardAuth
        authBuilder.AddPolicyScheme(GatewayAuthSchemes.DefaultScheme, "Gateway Smart Authentication", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader = context.Request.Headers.Authorization.ToString();

                // 1. Explicit Authorization headers have top priority (prevents ForwardAuth Header-Preemption DoS)
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return GatewayAuthSchemes.SelectJwtScheme(authHeader["Bearer ".Length..], gatewayOptions);
                }

                if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    if (gatewayOptions.Authentication.RequireKerberosOnly)
                    {
                        return NegotiateDefaults.AuthenticationScheme;
                    }
                    if (gatewayOptions.Authentication.BasicAuth.Enabled)
                    {
                        return GatewayAuthSchemes.Basic;
                    }
                    return NegotiateDefaults.AuthenticationScheme;
                }

                if (authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
                {
                    return NegotiateDefaults.AuthenticationScheme;
                }

                if (!gatewayOptions.Authentication.RequireKerberosOnly &&
                    authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                {
                    return NegotiateDefaults.AuthenticationScheme;
                }

                // 2. ForwardAuth (Traefik / Kubernetes Ingress) when enabled and proxy headers are present
                if (gatewayOptions.Authentication.ForwardAuth.Enabled)
                {
                    var fwdUserHeader = string.IsNullOrWhiteSpace(gatewayOptions.Authentication.ForwardAuth.UserHeader)
                        ? "X-Forwarded-User"
                        : gatewayOptions.Authentication.ForwardAuth.UserHeader;

                    if (context.Request.Headers.ContainsKey(fwdUserHeader) ||
                        context.Request.Headers.ContainsKey("X-Forwarded-User") ||
                        context.Request.Headers.ContainsKey("X-Auth-Request-User") ||
                        context.Request.Headers.ContainsKey("X-Forwarded-Preferred-Username"))
                    {
                        return GatewayAuthSchemes.ForwardAuth;
                    }
                }

                // 2b. F-AUTH-DX: session cookie (explicit Authorization headers above always win)
                if (isBasicSessionAllowed && context.Request.Cookies.ContainsKey(basicSessionCookieName))
                {
                    return BasicAuthSession.SchemeName;
                }

                // 3. Development Test Auth Simulation or Insecure Anonymous Access
#if AUTHERIS_TEST_AUTH
                if (isTestAuthAllowed)
                {
                    if (context.Request.Headers.ContainsKey("X-Test-User-Sid") ||
                        context.Request.Headers.ContainsKey("X-Test-AppId") ||
                        gatewayOptions.IsAnonymousAccessAllowed)
                    {
                        return TestAuthHandler.SchemeName;
                    }
                }
#endif

                var isHtml = context.Request.Headers.Accept.Any(a => a != null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
                if (isHtml && gatewayOptions.Authentication.BasicAuth.Enabled)
                {
                    // Browser request: Prefer Basic challenge when enabled so browser opens native dialog
                    return GatewayAuthSchemes.Basic;
                }

                // 4. Fallback challenge when unauthenticated
                if (gatewayOptions.Authentication.BasicAuth.Enabled &&
                    !gatewayOptions.Authentication.RequireKerberosOnly &&
                    string.IsNullOrWhiteSpace(authHeader))
                {
                    return GatewayAuthSchemes.Basic;
                }

                return NegotiateDefaults.AuthenticationScheme;
            };
        });

        // SEC M-03: Authenticated-user fallback policy and named role policies.
        services.AddAuthorization(GatewayPolicies.Configure);
        if (DevFeatures.Resolve(gatewayOptions, environment.IsDevelopment()).VerboseErrors)
        {
            // F-AUTH-DX: explain policy 403s and give JSON clients structured exception details (traceId)
            services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, DevAuthorizationResultHandler>();
            services.AddProblemDetails();
        }
        // O9: every unhandled exception becomes application/problem+json without details (all environments)
        services.AddProblemDetails();
        services.AddExceptionHandler<Autheris.Api.Middleware.GatewayExceptionHandler>();
        services.AddSingleton<Autheris.Application.Interfaces.IGatewayRoleEvaluator, Autheris.Application.Security.GatewayRoleEvaluator>();
        services.AddHttpContextAccessor();

        return services;
    }

    public static IServiceCollection AddGatewayGraphQL(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        bool demoDataEnabled = true)
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
        services.AddTransient<ICdnCachePurgeService, CloudflareCdnPurgeService>();

        services.AddFusionFederationServices(gatewayOptions);

        services.AddSingleton<ErrorSanitizingFilter>();
        // R-GQL-12: one module instance (and change-detection timer) for the application, also across schema rebuilds.
        services.AddSingleton(sp => new CatalogGraphQlTypeModule(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<ITableRelationRepository>(),
            sp.GetService<ILogger<CatalogGraphQlTypeModule>>(),
            TimeSpan.FromSeconds(gatewayOptions.GraphQL.CatalogSchemaRefreshSeconds)));
        services.AddSingleton<ISocketTokenValidator, JwtSocketTokenValidator>();
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
            var trustedDocuments = TrustedDocumentStore.LoadFromDirectory(gatewayOptions.GraphQL.TrustedDocumentsDirectory);
            services.AddSingleton(trustedDocuments);
            gqlBuilder.UseRequest<TrustedDocumentsOnlyMiddleware>();
        }

        if (demoDataEnabled)
        {
            // Sample content (finance/hr root fields, InvoiceRecord): only with demo data enabled
            gqlBuilder
                .AddTypeExtension<DemoQueryExtensions>()
                .AddTypeExtension<InvoiceRecordExtensions>();
        }

        gqlBuilder
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<Autheris.GraphQL.Interceptors.ReadOnlyOperationMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.DbtHealthExecutionMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.SchemaSunsettingExecutionMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.CdnCacheTagMiddleware>()
            .UseRequest<Autheris.GraphQL.Federation.SubgraphResultMaskingMiddleware>()
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
            .AddTypeModule(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>())
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddSubscriptionType<Subscription>()
            .AddInMemorySubscriptions()
            .AddSocketSessionInterceptor(sp => sp.GetRequiredService<WebSocketAuthInterceptor>())
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
                        : gatewayOptions.GraphQL.MaxRootFieldsPerOperation))
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

    internal static void ValidateGatewayOptions(GatewayOptions options, IHostEnvironment environment)
        => ValidateGatewayOptions(options, environment, System.Environment.GetEnvironmentVariable);

    internal static void ValidateGatewayOptions(GatewayOptions options, IHostEnvironment environment, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        bool isDevEnvironment = environment.IsDevelopment();
        ValidateObjectRecursively(options);

        // SEC E-01: the egress allowlist is validated in every environment; invalid or too broad entries abort the start.
        var egressErrors = EgressAllowlist.Validate(options.Egress);
        if (egressErrors.Count > 0)
        {
            throw new ValidationException(
                "Egress allowlist configuration error (Gateway:Egress): IPv4 networks at least /8, IPv6 at least /32, no overlap with " +
                "loopback/link-local/metadata/CGNAT/multicast/IPv4-mapped ranges:\n  - " + string.Join("\n  - ", egressErrors));
        }

        // POL-1 / F-7: Validate Casbin ModelPath whenever configured, or require it when Enabled
        if (!string.IsNullOrWhiteSpace(options.Casbin.ModelPath))
        {
            if (!File.Exists(options.Casbin.ModelPath))
            {
                throw new ValidationException($"Casbin model file '{options.Casbin.ModelPath}' was not found.");
            }
            if (new FileInfo(options.Casbin.ModelPath).Length == 0)
            {
                throw new ValidationException($"Casbin Model-Datei '{options.Casbin.ModelPath}' ist leer.");
            }

            try
            {
                CasbinModelContract.Verify(File.ReadAllText(options.Casbin.ModelPath));
            }
            catch (CasbinModelValidationException ex)
            {
                throw new ValidationException($"Casbin-Modell '{options.Casbin.ModelPath}' does not satisfy the gateway contract: {string.Join("; ", ex.Violations)}", ex);
            }
        }
        else if (options.Casbin.Enabled)
        {
            // SR15-50: Embedded default model is verified and allowed when ModelPath is not configured
            try
            {
                CasbinModelContract.Verify(CasbinEnforcementService.DefaultModelText);
            }
            catch (CasbinModelValidationException ex)
            {
                throw new ValidationException($"Embedded default Casbin model does not satisfy the gateway contract: {string.Join("; ", ex.Violations)}", ex);
            }
        }

        // POL-1 / R-POL-5: If Casbin is enabled, PolicyPath must exist, not be empty, and contain valid 'p' rules
        if (options.Casbin.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Casbin.PolicyPath))
            {
                throw new ValidationException("Casbin is enabled (Gateway:Casbin:Enabled = true), but Casbin:PolicyPath is not configured.");
            }
            if (!File.Exists(options.Casbin.PolicyPath))
            {
                throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' was not found.");
            }
            if (new FileInfo(options.Casbin.PolicyPath).Length == 0)
            {
                throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' is empty.");
            }

            // R-POL-5: Test-parse policy file at startup and verify that at least one 'p' rule exists (fail-closed)
            try
            {
                var policyText = File.ReadAllText(options.Casbin.PolicyPath);
                int totalPRules = Autheris.Application.Governance.CasbinEnforcementService.ValidatePolicyFile(policyText, modelSupportsWildcardTenant: true);
                if (totalPRules == 0)
                {
                    throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' contains no valid 'p' rules.");
                }
            }
            catch (ValidationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ValidationException($"Casbin is enabled, but policy file '{options.Casbin.PolicyPath}' is invalid: {ex.Message}", ex);
            }
        }

        // RR-L1-01: Validate ReverseProxy.KnownNetworks and KnownProxies against invalid formats and wildcard spoofing
        if (options.ReverseProxy.Enabled)
        {
            foreach (var netStr in options.ReverseProxy.KnownNetworks)
            {
                if (!System.Net.IPNetwork.TryParse(netStr, out var network))
                {
                    throw new ValidationException($"Configuration error ReverseProxy.KnownNetworks: invalid IP network '{netStr}'.");
                }

                if (network.PrefixLength == 0)
                {
                    throw new ValidationException($"Sicherheitsverletzung: ReverseProxy.KnownNetworks '{netStr}' ist ein Wildcard-Netzwerk (/0). Wildcard-Proxies sind verboten!");
                }
            }

            foreach (var proxyStr in options.ReverseProxy.KnownProxies)
            {
                if (!System.Net.IPAddress.TryParse(proxyStr, out _))
                {
                    throw new ValidationException($"Configuration error ReverseProxy.KnownProxies: invalid IP address '{proxyStr}'.");
                }
            }
        }

        // SEC C-04: Development disables most protections. Inside a container this is almost always an
        // accidentally shipped image default, so it requires an explicit opt-in.
        if (environment.IsDevelopment() &&
            IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")) &&
            !options.AllowDevelopmentInContainer &&
            !IsTruthy(getEnvironmentVariable("AUTHERIS_ALLOW_DEV_IN_CONTAINER")))
        {
            throw new ValidationException(
                "Security violation: ASPNETCORE_ENVIRONMENT=Development in a container (DOTNET_RUNNING_IN_CONTAINER=true) " +
                "is allowed only with an explicit opt-in (Gateway:AllowDevelopmentInContainer=true or AUTHERIS_ALLOW_DEV_IN_CONTAINER=true). " +
                "Use ASPNETCORE_ENVIRONMENT=Production for production; use docker-compose.dev.yml for local testing.");
        }

        // SEC H-08: PersistedQueriesOnly needs a trusted document store; otherwise the switch would be ineffective.
        if (options.GraphQL.PersistedQueriesOnly)
        {
            var dir = options.GraphQL.TrustedDocumentsDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(System.IO.Path.GetFullPath(dir)))
            {
                throw new ValidationException(
                    "Security violation: GraphQL.PersistedQueriesOnly=true requires an existing GraphQL.TrustedDocumentsDirectory " +
                    "containing the approved operations (*.graphql / *.gql). Without a document store, the switch would have no effect.");
            }
        }

        // SR-P2-02 / SEC-GQL-02: Federation context header signing key must be configured outside Development
        if (!environment.IsDevelopment() && options.Federation.Enabled && options.Federation.SignContextHeaders)
        {
            if (string.IsNullOrWhiteSpace(options.Federation.SigningKey) ||
                string.Equals(options.Federation.SigningKey, "autheris-federation-default-secret", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException(
                    "Security violation: Federation context header signing is enabled (Federation:SignContextHeaders = true), " +
                    "but Federation:SigningKey is not configured or uses the insecure default secret. A secure key of at least 32 bytes is required outside Development.");
            }

            Autheris.Application.Security.SecretKeyRequirements.EnsureMinimumLength(
                System.Text.Encoding.UTF8.GetBytes(options.Federation.SigningKey),
                "Gateway:Federation:SigningKey",
                isDevelopment: false);
        }

        // Security switch semantics: DANGER = blocked outside Development (see below), WARN = permitted everywhere
        // but reported loudly at startup, regular options = no message (see GatewayOptions.GetAllActiveBypasses).
        var dangerBypasses = options.GetActiveDangerBypasses();
        var warnings = options.GetActiveWarnings();
        if (dangerBypasses.Count > 0)
        {
            var bypasses = string.Join("\n  - ", dangerBypasses);
            Console.WriteLine(
                $"\n================================================================================\n" +
                $"⚠️⚠️⚠️  INSECURE GETTING-STARTED CONFIGURATION DETECTED  ⚠️⚠️⚠️\n" +
                $"The following security bypasses are currently ACTIVE:\n  - {bypasses}\n" +
                $"NEVER USE THESE INSECURE SETTINGS IN PRODUCTION ENVIRONMENTS!\n" +
                $"================================================================================\n");
        }

        if (warnings.Count > 0)
        {
            // WARN entries are permitted in Production; they are reported but never abort startup.
            Console.WriteLine(
                "[Autheris] WARNING: security-relevant settings are active (permitted, review regularly):\n  - " +
                string.Join("\n  - ", warnings));
        }

        if (!environment.IsDevelopment() && !options.VirtualFilters.RequireApproval)
        {
            Console.WriteLine("[Autheris] WARNING: VirtualFilters.RequireApproval is false outside Development. Four-eyes principle is recommended for production.");
        }


        if (options.WebSql.AllowDml && options.WebSql.DmlWriterRoles.Count == 0)
        {
            throw new ValidationException(
                "Configuration error: WebSql.AllowDml=true requires at least one role in WebSql.DmlWriterRoles " +
                "(SEC M-20: DML is allowed only for explicitly authorized roles).");
        }

        if (!environment.IsDevelopment() && options.IsQuickstartProfile)
        {
            throw new ValidationException("Security violation: The GettingStarted profile 'Quickstart' may be active ONLY in the Development environment.");
        }

        if (options.HighAvailability.ShutdownTimeoutSeconds < options.HighAvailability.QueryTimeoutSeconds + 10)
        {
            throw new ValidationException("NF-HA-01 violation: ShutdownTimeoutSeconds must be at least 10s greater than QueryTimeoutSeconds.");
        }

        if (options.HighAvailability.TerminationGracePeriodSeconds < options.HighAvailability.DrainDelaySeconds + options.HighAvailability.ShutdownTimeoutSeconds + 10)
        {
            throw new ValidationException("NF-HA-01 violation: TerminationGracePeriodSeconds must be greater than DrainDelay + ShutdownTimeout + 10s.");
        }

        var devErrors = DevOptionsValidator.Validate(options, isDevEnvironment);
        if (devErrors.Count > 0)
        {
            throw new ValidationException(string.Join("\n", devErrors));
        }

        // F-AUTH-DX: session cookie guards (environment allowlist, never Production, no wildcard CORS, shared keys)
        var basicSessionErrors = BasicAuthSession.Validate(options, environment);
        if (basicSessionErrors.Count > 0)
        {
            throw new ValidationException(string.Join("\n", basicSessionErrors));
        }

        // R2-1 / N-1: Basic-auth users need a password; SIDs must not contain ':' or start with 'ITSM'
        if (options.Authentication.BasicAuth.Enabled)
        {
            var sidErrors = BasicAuthSession.ValidateUsers(options.Authentication.BasicAuth);
            if (sidErrors.Count > 0)
            {
                throw new ValidationException(string.Join("\n", sidErrors));
            }
        }

        // Review (Low): a malformed default tenant must abort startup instead of degrading to the legacy tenant.
        if (options.Authentication.ForwardAuth.Enabled &&
            !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.DefaultTenantId) &&
            !Autheris.Domain.Common.TenantId.TryParse(options.Authentication.ForwardAuth.DefaultTenantId, out _))
        {
            throw new ValidationException("ForwardAuth.DefaultTenantId has an invalid tenant format.");
        }

        if (options.Authentication.RequireKerberosOnly && options.Authentication.BasicAuth.Enabled)
        {
            throw new ValidationException("Security conflict: BasicAuth must not be enabled when RequireKerberosOnly is set to true.");
        }

        if (!environment.IsDevelopment() && options.Authentication.ForwardAuth.Enabled)
        {
            var hasSecret = !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecret) ||
                            !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecretKeyVaultRef);
            if (!hasSecret)
            {
                throw new ValidationException("Security violation: Outside Development, ForwardAuth requires a configured SharedSecret or SharedSecretKeyVaultRef.");
            }

            if (!string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecret) &&
                System.Text.Encoding.UTF8.GetByteCount(options.Authentication.ForwardAuth.SharedSecret) < 32)
            {
                throw new ValidationException("Security violation: Outside the development environment, ForwardAuth.SharedSecret must be at least 32 bytes long.");
            }

            if (!options.Authentication.ForwardAuth.RequireTrustedProxy)
            {
                throw new ValidationException("Security violation: RequireTrustedProxy must not be set to false when ForwardAuth is enabled outside Development.");
            }

            if (options.Authentication.ForwardAuth.TrustedNetworks != null)
            {
                foreach (var netStr in options.Authentication.ForwardAuth.TrustedNetworks)
                {
                    if (!System.Net.IPNetwork.TryParse(netStr, out var network))
                    {
                        throw new ValidationException($"Configuration error ForwardAuth.TrustedNetworks: invalid IP network '{netStr}'.");
                    }

                    if (network.PrefixLength == 0)
                    {
                        throw new ValidationException($"Sicherheitsverletzung (API-8): ForwardAuth.TrustedNetworks '{netStr}' ist ein Wildcard-Netzwerk (/0). Wildcard-Netzwerke sind verboten!");
                    }
                }
            }

            if (options.Authentication.ForwardAuth.TrustedProxies != null)
            {
                foreach (var proxyStr in options.Authentication.ForwardAuth.TrustedProxies)
                {
                    if (!System.Net.IPAddress.TryParse(proxyStr, out _))
                    {
                        throw new ValidationException($"Configuration error ForwardAuth.TrustedProxies: invalid IP address '{proxyStr}'.");
                    }
                }
            }
        }

        if (!environment.IsDevelopment())
        {
            if (options.GovernanceDb.SeedDemoData == true)
            {
                throw new ValidationException("Sicherheitsverletzung: GovernanceDb.SeedDemoData darf AUSSCHLIESSLICH in der Development-Umgebung true sein!");
            }

            if (options.Authentication.EnableTestAuthHandler)
            {
                throw new ValidationException("Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.");
            }

            // SEC SG-37: RequireHttpsMetadata must not be false outside Development.
            if (options.Authentication.EntraId.RequireHttpsMetadata == false ||
                options.Authentication.Adfs.RequireHttpsMetadata == false)
            {
                throw new ValidationException("Security violation: RequireHttpsMetadata must not be false outside Development.");
            }

            if (options.IsAnonymousAccessAllowed)
            {
                throw new ValidationException("Security violation: danger_allow_anonymous_access may be true ONLY in the Development environment.");
            }

            if (options.Rebac.SeedTuples.Count > 0)
            {
                if (options.Rebac.SeedTuples.Any(t => t.User.Contains("david", StringComparison.OrdinalIgnoreCase) || t.Object.Contains("prod", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ValidationException("Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.");
                }
            }

            // Only DANGER entries are blocked outside Development; WARN entries are permitted (reported above).
            if (dangerBypasses.Count > 0)
            {
                throw new ValidationException(
                    $"Critical security violation: The following security bypasses may be active ONLY in the Development environment:\n  - " +
                    string.Join("\n  - ", dangerBypasses));
            }
        }

        if (!environment.IsDevelopment())
        {
            // RR-L3-05: warn_enable_introspection and GraphQL:EnableIntrospection are prohibited outside Development without explicit opt-in
            if ((options.IsIntrospectionForced || options.GraphQL.EnableIntrospection) && !options.AllowInsecureWarnFlagsInProduction)
            {
                throw new ValidationException(
                    "Security violation: Outside Development, GraphQL introspection (GraphQL:EnableIntrospection or warn_enable_introspection) " +
                    "may be active only with an explicit opt-in (AllowInsecureWarnFlagsInProduction = true).");
            }

            // API-16: warn_allow_all_cors_origins is prohibited outside Development without explicit opt-in
            if (options.IsAllCorsAllowed && !options.AllowInsecureWarnFlagsInProduction)
            {
                throw new ValidationException(
                    "Security violation (API-16): Outside Development, warn_allow_all_cors_origins may be active only with an explicit " +
                    "opt-in (AllowInsecureWarnFlagsInProduction = true).");
            }

            // Sicherheits-Invariante 2: Mcp.EnableDeveloperCors is strictly prohibited outside Development and cannot be bypassed
            if (options.Mcp.EnableDeveloperCors)
            {
                throw new ValidationException(
                    "Security violation: Mcp.EnableDeveloperCors is strictly prohibited outside Development and cannot be bypassed.");
            }

            if (!options.IsInsecureTransportAllowed && !options.IsColumnMaskingDisabled &&
                (string.IsNullOrWhiteSpace(options.DataMasking.HmacSecretKeyVaultRef) ||
                options.DataMasking.HmacSecretKeyVaultRef == "DEV_INSECURE_TEST_KEY_ONLY" ||
                options.DataMasking.HmacSecretKeyVaultRef == "dev-only-hmac-salt-secure-fallback"))
            {
                throw new ValidationException("NF-SEC-03 violation: Outside Development, HmacSecretKeyVaultRef must be a valid Key Vault secret reference.");
            }

            if (!options.IsInsecureTransportAllowed && options.OpenMetadata.Enabled &&
                Uri.TryCreate(options.OpenMetadata.ServerUrl, UriKind.Absolute, out var omUri) &&
                !string.Equals(omUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException("Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.");
            }

            if (options.AreUntrustedCertificatesAllowed)
            {
                throw new ValidationException("Security violation: danger_allow_untrusted_certificates may be true ONLY in the Development environment.");
            }

            // DEP-7 / INF-6: data source connections must encrypt and verify the server certificate outside Development.
            foreach (var (name, connection) in options.DataSources.Connections)
            {
                if (Autheris.Infrastructure.Persistence.ConnectionTlsPolicy.Validate(connection.Provider, connection.ConnectionString) is { } tlsError)
                {
                    throw new ValidationException($"Sicherheitsverletzung: DataSources:Connections:{name}: {tlsError}");
                }
            }

            if (options.GraphQL.TrustedOrigins.Contains("*"))
            {
                throw new ValidationException("Security violation: TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection).");
            }

            if (options.GraphQL.TrustedOrigins.Any(o => o != "*" && (!Uri.TryCreate(o, UriKind.Absolute, out var u) || !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))))
            {
                throw new ValidationException("Security violation: Outside Development, TrustedOrigins must contain only HTTPS URLs.");
            }

            if (options.HighAvailability.MultiNodeClusterMode && options.Rebac.Enabled && !options.Caching.Redis.Enabled)
            {
                throw new ValidationException("Security violation: ReBAC in MultiNodeClusterMode requires Caching.Redis.Enabled = true for cluster-wide invalidation (RR-L4-04).");
            }

            if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) && !options.Caching.Redis.Enabled)
            {
                throw new ValidationException("NF-HA-02 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide cache and epoch invalidation requires Caching.Redis.Enabled = true.");
            }

            if (!string.IsNullOrWhiteSpace(options.Plugins.Directory) &&
                Directory.Exists(System.IO.Path.GetFullPath(options.Plugins.Directory)) &&
                !options.Plugins.RequireIntegrityManifest)
            {
                throw new ValidationException("Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.");
            }

            if (options.Authentication.BasicAuth.Enabled)
            {
                if (options.Authentication.BasicAuth.Users.Any(u => string.IsNullOrWhiteSpace(u.Password) || (!u.Password.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase) && !u.Password.StartsWith("$argon2id$", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new ValidationException("Security violation (DEP-14): Outside Development, BasicAuth passwords must be stored as a hash ($argon2id$... or $pbkdf2$...). Plaintext passwords are prohibited.");
                }

                // RR-L2-03: weak work factors (e.g. $pbkdf2$1$...) are rejected at boot.
                var minIterations = Math.Max(210_000, options.Authentication.BasicAuth.MinimumPbkdf2Iterations);
                if (options.Authentication.BasicAuth.Users.Any(u =>
                        u.Password.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase) &&
                        (u.Password.Split('$') is not { Length: 5 } parts ||
                         !int.TryParse(parts[2], out var iterations) ||
                         iterations < minIterations)))
                {
                    throw new ValidationException($"Security violation: Outside Development, BasicAuth PBKDF2 hashes must use at least {minIterations} iterations.");
                }
            }

            // SEC M-02: Fail-closed when JWT is active without audience or issuer.
            if (options.Authentication.EntraId.Enabled &&
                ((string.IsNullOrWhiteSpace(options.Authentication.EntraId.Audience) && string.IsNullOrWhiteSpace(options.Authentication.EntraId.ClientId)) ||
                 string.IsNullOrWhiteSpace(options.Authentication.EntraId.TenantId)))
            {
                throw new ValidationException("Security violation: Outside Development, EntraId requires Audience or ClientId, plus TenantId (issuer), to be configured.");
            }

            if (options.Authentication.Adfs.Enabled && (string.IsNullOrWhiteSpace(options.Authentication.Adfs.Audience) || string.IsNullOrWhiteSpace(options.Authentication.Adfs.Authority)))
            {
                throw new ValidationException("Security violation: Outside Development, Adfs.Audience and Authority must be configured.");
            }

            if (options.Audit.Worm.Enabled && string.Equals(options.Audit.Worm.StorageType, "S3", StringComparison.OrdinalIgnoreCase) && options.Audit.Worm.EnforceObjectLock &&
                (string.IsNullOrWhiteSpace(options.Audit.Worm.S3AccessKey) || string.IsNullOrWhiteSpace(options.Audit.Worm.S3SecretKey)))
            {
                throw new ValidationException("Security violation: Outside Development, S3 WORM with EnforceObjectLock requires S3AccessKey and S3SecretKey to be configured.");
            }

            if (DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite) &&
                !string.IsNullOrWhiteSpace(options.GovernanceDb.ConnectionString) &&
                (options.GovernanceDb.ConnectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                 options.GovernanceDb.ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ValidationException("Security violation: In-memory SQLite databases (GovernanceDb.ConnectionString) are strictly prohibited outside Development.");
            }

            // DEP-4: In container environments, a relative SQLite database path in /app is unwritable for non-root APP_UID
            if (IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")) &&
                DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite))
            {
                try
                {
                    var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.GovernanceDb.ConnectionString);
                    if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
                        !builder.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) &&
                        builder.Mode != Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
                    {
                        var isDirectRootFile = !builder.DataSource.Contains('/') && !builder.DataSource.Contains('\\');
                        var isAppRoot = builder.DataSource.Equals("/app/governance.db", StringComparison.OrdinalIgnoreCase);
                        if (isDirectRootFile || isAppRoot)
                        {
                            throw new ValidationException(
                                "Security and configuration error (DEP-4): In the container, the process runs as non-root user ($APP_UID). " +
                                $"The SQLite path '{builder.DataSource}' is located directly in the non-writable application directory (/app). " +
                                "Use the writable data directory '/app/data' (e.g. 'Data Source=/app/data/governance.db;Cache=Shared').");
                        }
                    }
                }
                catch (ValidationException)
                {
                    throw;
                }
                catch
                {
                    // Ignore parse errors here; SqliteConnection will handle them
                }
            }

            // AU-01 & AU-03: Startup Fail-Closed & Umgebungsvalidierung für Audit
            var envName = getEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? environment.EnvironmentName;
            bool isDevOrTest = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

            bool isSqliteFile = false;
            if (DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite) &&
                !string.IsNullOrWhiteSpace(options.GovernanceDb.ConnectionString))
            {
                try
                {
                    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.GovernanceDb.ConnectionString);
                    isSqliteFile = !string.IsNullOrWhiteSpace(csb.DataSource) &&
                                   !csb.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) &&
                                   csb.Mode != Microsoft.Data.Sqlite.SqliteOpenMode.Memory;
                }
                catch { }
            }

            if (!isDevOrTest)
            {
                if (options.Audit.HmacKeyIsFallback)
                {
                    throw new ValidationException(
                        "CRITICAL SECURITY VIOLATION: Hardcoded or fallback HMAC audit keys are strictly prohibited in production.");
                }

                if (!isSqliteFile)
                {
                    // Produktion: Leerer Wert gilt zwingend als Produktion!
                    if (string.IsNullOrWhiteSpace(options.Audit.ChainAnchorPath) &&
                        string.IsNullOrWhiteSpace(options.Audit.ChainAnchorWormDirectory) &&
                        string.IsNullOrWhiteSpace(options.Audit.ChainAnchorSignerKeyVaultRef))
                    {
                        throw new ValidationException(
                            "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                            "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                            "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
                    }
                }
            }
        }

        if (!IsSupportedGovernanceDbProvider(options.GovernanceDb.Provider))
        {
            throw new ValidationException($"GovernanceDb provider '{options.GovernanceDb.Provider}' is not supported. Allowed values are 'Sqlite', 'PostgreSql' or 'SqlServer'.");
        }

        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) &&
            DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            throw new ValidationException("Security violation (E-2): Multi-node cluster mode and more than 1 replica are not allowed with SQLite, because SQLite uses local database files per instance. Configure GovernanceDb.Provider = 'PostgreSql' or 'SqlServer' for cluster operation.");
        }
    }

    /// <summary>Architecture 5: the governance store runs on SQLite, PostgreSQL or SQL Server, under any provider alias.</summary>
    private static bool IsSupportedGovernanceDbProvider(string? provider) =>
        DataSourceProvider.Is(provider, DatabaseDialect.Sqlite) || DataSourceProvider.Is(provider, DatabaseDialect.PostgreSql) || DataSourceProvider.Is(provider, DatabaseDialect.SqlServer);

    private static bool IsTruthy(string? value)
    {
        var trimmed = value?.Trim();
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "1", StringComparison.Ordinal);
    }

    private static void ValidateObjectRecursively(object instance)
    {
        var context = new ValidationContext(instance);
        Validator.ValidateObject(instance, context, validateAllProperties: true);

        foreach (var prop in instance.GetType().GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !prop.PropertyType.IsArray && !typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                var val = prop.GetValue(instance);
                if (val != null)
                {
                    ValidateObjectRecursively(val);
                }
            }
        }
    }
}
