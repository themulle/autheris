namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Linq;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Caching.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Diagnostics;
using Autheris.Infrastructure.Garnet;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Idempotency;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
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
            // Do NOT call StartServer() here; it will be called asynchronously in IHostedService.StartAsync.
            services.AddSingleton<IGarnetServerManager, GarnetServerManager>();
            services.AddHostedService(sp => (GarnetServerManager)sp.GetRequiredService<IGarnetServerManager>());

            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var garnetManager = sp.GetRequiredService<IGarnetServerManager>();
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
            services.AddSingleton<IDistributedClusterStateProvider, Autheris.Infrastructure.State.RedisClusterStateProvider>();
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
            services.AddSingleton<IDistributedClusterStateProvider, Autheris.Infrastructure.State.RedisClusterStateProvider>();
        }
        else
        {
            services.AddSingleton<IEventBus, InProcessChannelEventBus>();
            services.AddSingleton<IRateLimiterService, InMemoryRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
            services.AddSingleton<ITokenRevocationService, InMemoryTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddSingleton<IDistributedClusterStateProvider, InMemoryClusterStateProvider>();
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
        services.AddSingleton<IEpochValidationService, EpochValidationService>();
        services.AddSingleton<IParameterBudgetProvider, DatabaseParameterBudgetProvider>();

        // SQL Connection Factory & Health Checks
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<AuditChainIntegrityMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditChainIntegrityMonitor>());
        // SEC R3-4: anonymous /health/ready must not drive the DB probe per request -> cached, single-flight decorator.
        services.AddSingleton<GatewayHealthCheckService>();
        services.AddSingleton<IGatewayHealthCheckService>(sp =>
            new CachedGatewayHealthCheckService(sp.GetRequiredService<GatewayHealthCheckService>()));

        return services;
    }
}
