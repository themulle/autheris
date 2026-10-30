namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Linq;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Caching.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Databricks;
using Autheris.Domain.Diagnostics;
using Autheris.Infrastructure.Garnet;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Idempotency;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

public static class GatewayStorageServiceExtensions
{
    public static IServiceCollection AddAutherisStorage(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = (long)gatewayOptions.Caching.L1MemoryCache.SizeLimitMb * 1024 * 1024;
        });

        services.AddSingleton<IBinaryCacheSerializer, MemoryPackCacheSerializer>();

        if (gatewayOptions.Caching.Garnet.EnableEmbeddedServer)
        {
            // SEC H-01 / AR-06: Register GarnetServerManager as singleton and hosted service.
            services.AddSingleton<IGarnetServerManager, GarnetServerManager>();
            services.AddHostedService(sp => (GarnetServerManager)sp.GetRequiredService<IGarnetServerManager>());

            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var garnetManager = sp.GetRequiredService<IGarnetServerManager>();
                // SEC H-01 / AR-06: Imperatively ensure Garnet is running before establishing RESP connection
                // to prevent race conditions when services resolve multiplexer before HostedService.StartAsync.
                garnetManager.StartServer();

                var garnetConfig = new ConfigurationOptions
                {
                    EndPoints = { $"{gatewayOptions.Caching.Garnet.Host}:{gatewayOptions.Caching.Garnet.Port}" },
                    Password = garnetManager.ClientPassword, // SEC H-01: Garnet runs with --auth Password
                    ConnectTimeout = gatewayOptions.Caching.Redis.ConnectTimeoutMs,
                    SyncTimeout = gatewayOptions.Caching.Redis.SyncTimeoutMs,
                    AbortOnConnectFail = false
                };
                RedisConnectionSecurity.ApplyGarnetClientTls(garnetConfig, gatewayOptions.Caching.Garnet); // SEC H-01: optional TLS
                return ConnectionMultiplexer.Connect(garnetConfig);
            });
            services.AddSingleton<IEventBus, RedisEventBus>();
            services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
            services.AddSingleton<ITokenRevocationService, RedisTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddClusterState(ClusterStateMode.Embedded);
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
            services.AddClusterState(ClusterStateMode.External);
        }
        else
        {
            services.AddSingleton<IEventBus, InProcessChannelEventBus>();
            services.AddSingleton<IRateLimiterService, InMemoryRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
            services.AddSingleton<ITokenRevocationService, InMemoryTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddClusterState(ClusterStateMode.InMemory);
        }

        var isDev = environment?.IsDevelopment() ?? false;
        if (!isDev && DataSourceProvider.Is(gatewayOptions.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            var loggerFactory = services.FirstOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Logging.ILoggerFactory))?.ImplementationInstance as Microsoft.Extensions.Logging.ILoggerFactory;
            var startupLogger = loggerFactory?.CreateLogger("Autheris.Startup");
            startupLogger?.LogWarning("AR-08: SQLite governance provider is not recommended for production environments. Consider PostgreSQL or SQL Server.");
        }

        services.AddSingleton<ITableSensitivityLookup>(sp => new TableMetadataSensitivityLookup(
            () => sp.GetService<ITableMetadataRepository>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TableMetadataSensitivityLookup>>()));
        services.AddSingleton<EpochValidationService>();
        services.AddSingleton<IEpochValidationService>(sp => sp.GetRequiredService<EpochValidationService>());
        services.AddSingleton<IParameterBudgetProvider, DatabaseParameterBudgetProvider>();

        // SQL Connection Factory & Health Checks
        // WP-X0j: Databricks is opened through the REST facade over the Statement Execution client. Wired but Experimental: it is not
        // admitted to WebSQL (SqlDialectMapper.IsExecutable, S-4) until a green G9 run.
        services.AddDatabricksStatementExecution();
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<ISqlSourceDescriber, Autheris.Infrastructure.Persistence.SqlSourceDescriber>();
        // SEC-X1-10: SQLite statements are interrupted at the execution timeout and on cancellation.
        services.AddSingleton<Autheris.Application.Sql.Interfaces.IProviderExecutionGuard, SqliteProviderExecutionGuard>();

        // WP-X0c (CR-ADG-47): column types and collations from database metadata. The TTL is validated here, so a value above
        // the 300 s maximum fails the startup (SEC-X1-09). Nothing consumes the provider before X1.
        var catalogOptions = new GovernedCatalogOptions { CacheTtlSeconds = gatewayOptions.DataSources.CatalogCacheTtlSeconds };
        catalogOptions.Validate();
        services.AddSingleton(catalogOptions);
        services.AddSingleton<IColumnMetadataReader, PostgreSqlColumnMetadataReader>();
        services.AddSingleton<IColumnMetadataReader, SqlServerColumnMetadataReader>();
        services.AddSingleton<IColumnMetadataReader, OracleColumnMetadataReader>();
        services.AddSingleton<IColumnMetadataReader, SqliteColumnMetadataReader>();
        services.AddSingleton<IColumnMetadataReader, DuckDbColumnMetadataReader>();
        services.AddSingleton<IColumnMetadataReader, DatabricksColumnMetadataReader>();
        // I403: key and index column direction. Only SQL Server and PostgreSQL expose a direction; no other dialect has a reader.
        services.AddSingleton<IKeyIndexMetadataReader, PostgreSqlKeyIndexMetadataReader>();
        services.AddSingleton<IKeyIndexMetadataReader, SqlServerKeyIndexMetadataReader>();
        services.AddSingleton<GovernedCatalogProvider>();
        services.AddSingleton<IGovernedCatalogProvider>(sp => sp.GetRequiredService<GovernedCatalogProvider>());
        services.AddSingleton<AuditChainIntegrityMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditChainIntegrityMonitor>());
        // SEC R3-4: anonymous /health/ready must not drive the DB probe per request -> cached, single-flight decorator.
        services.AddSingleton<GatewayHealthCheckService>();
        services.AddSingleton<IGatewayHealthCheckService>(sp =>
            new CachedGatewayHealthCheckService(sp.GetRequiredService<GatewayHealthCheckService>()));

        return services;
    }
}
