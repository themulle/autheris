namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Linq;
using Autheris.Api.Endpoints.Health;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.State.Caching.Services;
using Autheris.Application.Common.Interfaces;
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

        if (gatewayOptions.Caching.Redis.Enabled)
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
            services.AddSingleton<Autheris.Application.Policy.WriteGrants.IWriteBudgetStore>(sp =>
                new DistributedCacheWriteBudgetStore(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetService<TimeProvider>()));
            services.AddSingleton<ITokenRevocationService, RedisTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddClusterState(ClusterStateMode.External);
        }
        else
        {
            services.AddSingleton<IEventBus, InProcessChannelEventBus>();
            services.AddSingleton<IRateLimiterService, InMemoryRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
            services.AddSingleton<Autheris.Application.Policy.WriteGrants.IWriteBudgetStore, InMemoryWriteBudgetStore>();
            services.AddSingleton<ITokenRevocationService, InMemoryTokenRevocationService>(); // SEC M-14 (GAP-B)
            services.AddClusterState(ClusterStateMode.InMemory);
        }

        services.AddSingleton<ITableSensitivityLookup>(sp => new TableMetadataSensitivityLookup(
            () => sp.GetService<ITableMetadataRepository>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TableMetadataSensitivityLookup>>(),
            () => sp.GetService<IClassificationRepository>()));
        services.AddSingleton<EpochValidationService>();
        services.AddSingleton<IEpochValidationService>(sp => sp.GetRequiredService<EpochValidationService>());
        services.AddSingleton<IParameterBudgetProvider, DatabaseParameterBudgetProvider>();

        // SQL Connection Factory & Health Checks
        // WP-X0j: Databricks is opened through the REST facade over the Statement Execution client. Wired but Experimental: it is not
        // admitted to WebSQL (SqlDialectMapper.IsExecutable, S-4) until a green G9 run.
        services.AddDatabricksStatementExecution();
        // Issue #1122: the Unity Catalog reconciler (idles unless a Databricks data source has a provisioning section).
        services.AddScoped<Autheris.Application.Governance.Provisioning.IUcTableContextSource, Autheris.Application.Governance.Provisioning.UcTableContextSource>();
        services.AddScoped<Autheris.Application.Governance.Provisioning.UcProvisioningReconciler>();
        services.AddHostedService<Autheris.Application.Governance.Provisioning.UcProvisioningService>();
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<ISqlSourceDescriber, Autheris.Infrastructure.Persistence.SqlSourceDescriber>();
        services.AddSingleton<Autheris.Application.Sql.Interfaces.IProviderExecutionGuard>(
            Autheris.Application.Sql.Interfaces.PassThroughExecutionGuard.Instance);

        // WP-X0c (CR-ADG-47): column types and collations from database metadata. The TTL is validated here, so a value above
        // the 300 s maximum fails the startup. Nothing consumes the provider before X1.
        var catalogOptions = new GovernedCatalogOptions { CacheTtlSeconds = gatewayOptions.DataSources.CatalogCacheTtlSeconds };
        catalogOptions.Validate();
        services.AddSingleton(catalogOptions);
        services.AddSingleton<GovernedCatalogProvider>();
        services.AddSingleton<IGovernedCatalogProvider>(sp => sp.GetRequiredService<GovernedCatalogProvider>());
        services.AddSingleton<AuditChainIntegrityMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditChainIntegrityMonitor>());
        services.AddHostedService<Autheris.Api.Configuration.Validation.DmlIdempotencyStoreStartupCheck>();
        // Anonymous /health/ready uses ReadinessProbe (2 s single-flight cache + 5 s shared timeout) over GatewayHealthCheckService.
        services.AddSingleton<GatewayHealthCheckService>();
        services.AddSingleton<IGatewayHealthCheckService>(sp =>
            sp.GetRequiredService<GatewayHealthCheckService>());
        services.AddSingleton<ReadinessProbe>();

        return services;
    }
}
