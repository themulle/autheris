using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Governance;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Engine-agnostic governance store harness resolved through the production DI registration
/// (<see cref="GatewayGovernanceServiceExtensions.AddAutherisGovernance"/>).
/// </summary>
public sealed class GovernanceTestStore : IAsyncDisposable
{
    private readonly ServiceProvider? _serviceProvider;
    private readonly MutableCallerContextProvider _callerProvider;
    private readonly string _anchorDirectory;
    private readonly bool _ownsAnchorDirectory;

    private GovernanceTestStore(
        string engine,
        string provider,
        string connectionString,
        ServiceProvider? serviceProvider,
        MutableCallerContextProvider callerProvider,
        string anchorDirectory,
        bool ownsAnchorDirectory,
        bool isAvailable)
    {
        Engine = engine;
        Provider = provider;
        ConnectionString = connectionString;
        _serviceProvider = serviceProvider;
        _callerProvider = callerProvider;
        _anchorDirectory = anchorDirectory;
        _ownsAnchorDirectory = ownsAnchorDirectory;
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }
    public string Engine { get; }
    public string Provider { get; }
    public bool IsSqlServer => string.Equals(Engine, "sqlserver", StringComparison.OrdinalIgnoreCase);
    public string ConnectionString { get; }
    public string AnchorDirectory => _anchorDirectory;

    public TenantId TenantA { get; } = new("tenant-a");
    public TenantId TenantB { get; } = new("tenant-b");

    public IServiceProvider Services =>
        _serviceProvider ?? throw new InvalidOperationException($"GovernanceTestStore ({Engine}) is not available.");

    public IGovernanceRepository Repository => Services.GetRequiredService<IGovernanceRepository>();
    public IGovernanceStoreInitializer StoreInitializer => Services.GetRequiredService<IGovernanceStoreInitializer>();
    public IGovernanceMigrationRunner MigrationRunner => Services.GetRequiredService<IGovernanceMigrationRunner>();
    public ITableMetadataRepository TableMetadata => Services.GetRequiredService<ITableMetadataRepository>();
    public IConsentRepository Consents => Services.GetRequiredService<IConsentRepository>();
    public IConsentApprovalRepository ConsentApprovals => Services.GetRequiredService<IConsentApprovalRepository>();
    public IConsentDescriptionRepository ConsentDescriptions => Services.GetRequiredService<IConsentDescriptionRepository>();
    public IAuditLogRepository AuditLogs => Services.GetRequiredService<IAuditLogRepository>();
    public IAuditChainExportSource AuditExport => Services.GetRequiredService<IAuditChainExportSource>();
    public IAuditPurgeKeyProvider AuditPurgeKey => Services.GetRequiredService<IAuditPurgeKeyProvider>();
    public IAuditChainAnchorStore AuditAnchorStore => Services.GetRequiredService<IAuditChainAnchorStore>();
    public IPolicyEpochRepository PolicyEpochs => Services.GetRequiredService<IPolicyEpochRepository>();
    public IDataOwnershipRepository DataOwnership => Services.GetRequiredService<IDataOwnershipRepository>();
    public IDataOwnerAssignmentRepository DataOwnerAssignments => Services.GetRequiredService<IDataOwnerAssignmentRepository>();
    public IPhysicalIndexRepository PhysicalIndexes => Services.GetRequiredService<IPhysicalIndexRepository>();
    public ITableRelationRepository TableRelations => Services.GetRequiredService<ITableRelationRepository>();
    public IVirtualFilterRepository VirtualFilters => Services.GetRequiredService<IVirtualFilterRepository>();
    public ICallerAttributeRepository CallerAttributes => Services.GetRequiredService<ICallerAttributeRepository>();
    public ITrustedDocumentRepository TrustedDocuments => Services.GetRequiredService<ITrustedDocumentRepository>();
    public IAccessProfileRepository AccessProfiles => Services.GetRequiredService<IAccessProfileRepository>();
    public IIdentityStoreRepository IdentityStore => Services.GetRequiredService<IIdentityStoreRepository>();
    public IFunctionalRightRepository FunctionalRights => Services.GetRequiredService<IFunctionalRightRepository>();
    public IRecertificationRepository Recertification => Services.GetRequiredService<IRecertificationRepository>();
    public ICdcWatermarkRepository CdcWatermarks => Services.GetRequiredService<ICdcWatermarkRepository>();
    public IConsumerAggregationRepository ConsumerAggregation => Services.GetRequiredService<IConsumerAggregationRepository>();
    public ISemanticManifestStore SemanticManifests => Services.GetRequiredService<ISemanticManifestStore>();
    public ICdcStreamStore CdcStreams => Services.GetRequiredService<ICdcStreamStore>();
    public ICdcStreamKeyStore CdcStreamKeys => Services.GetRequiredService<ICdcStreamKeyStore>();
    public ICloudEventSubscriptionStore CloudEventSubscriptions => Services.GetRequiredService<ICloudEventSubscriptionStore>();

    public void SetCaller(CallerContext? caller) => _callerProvider.Current = caller;

    public void InvalidateMetadataCache()
    {
        if (Repository is PostgreSqlGovernanceRepository pg)
        {
            pg.InvalidateMetadataCacheForTesting();
        }
        else if (Repository is SqlServerGovernanceRepository mssql)
        {
            mssql.InvalidateMetadataCacheForTesting();
        }
    }

    public void SetConsentStatusUpdateSeam(Func<DbConnection, DbTransaction, CancellationToken, Task>? seam)
    {
        if (Repository is PostgreSqlGovernanceRepository pg)
        {
            pg.ConsentStatusUpdateSeam = seam;
        }
        else if (Repository is SqlServerGovernanceRepository mssql)
        {
            mssql.ConsentStatusUpdateSeam = seam;
        }
    }

    public static async Task<GovernanceTestStore> CreatePostgreSqlAsync(
        GovernancePostgresFixture fixture,
        string? dbPrefix = null,
        Action<GatewayOptions>? configure = null,
        CallerContext? caller = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.IsAvailable)
        {
            return CreateUnavailable("postgresql", "PostgreSql");
        }

        var connStr = await fixture.CreateDatabaseAsync(dbPrefix ?? "contract").ConfigureAwait(false);
        return await CreateFromConnectionStringAsync(
            provider: "PostgreSql",
            connectionString: connStr,
            runMigrations: false,
            initializeStore: true,
            configure: configure,
            caller: caller).ConfigureAwait(false);
    }

    public static async Task<GovernanceTestStore> CreateSqlServerAsync(
        GovernanceSqlServerFixture fixture,
        string? dbPrefix = null,
        Action<GatewayOptions>? configure = null,
        CallerContext? caller = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.IsAvailable)
        {
            return CreateUnavailable("sqlserver", "SqlServer");
        }

        var connStr = await fixture.CreateDatabaseAsync(dbPrefix ?? "contract").ConfigureAwait(false);
        return await CreateFromConnectionStringAsync(
            provider: "SqlServer",
            connectionString: connStr,
            runMigrations: false,
            initializeStore: true,
            configure: configure,
            caller: caller).ConfigureAwait(false);
    }

    public static async Task<GovernanceTestStore> CreateFromConnectionStringAsync(
        string provider,
        string connectionString,
        bool runMigrations = false,
        bool initializeStore = true,
        bool seedDemoData = false,
        Action<GatewayOptions>? configure = null,
        CallerContext? caller = null,
        string? anchorDir = null)
    {
        var ownsAnchorDir = string.IsNullOrWhiteSpace(anchorDir);
        var resolvedAnchorDir = ownsAnchorDir
            ? Path.Combine(Path.GetTempPath(), "autheris_contract_anchor_" + Guid.NewGuid().ToString("N"))
            : anchorDir!;
        Directory.CreateDirectory(resolvedAnchorDir);

        var engine = string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase) ? "sqlserver" : "postgresql";
        var gatewayOptions = new GatewayOptions
        {
            Audit = new AuditOptions
            {
                ChainAnchorPath = Path.Combine(resolvedAnchorDir, "audit.anchor")
            },
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = provider,
                ConnectionString = connectionString,
                SeedDemoData = seedDemoData,
                AuditHmacKeyVaultRef = "test-audit-hmac-secret",
                Migrations = new GovernanceMigrationOptions
                {
                    Mode = GovernanceMigrationMode.Apply
                }
            }
        };
        configure?.Invoke(gatewayOptions);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:GovernanceDb:Provider"] = gatewayOptions.GovernanceDb.Provider,
                ["Gateway:GovernanceDb:ConnectionString"] = gatewayOptions.GovernanceDb.ConnectionString,
                ["Gateway:CdcWebhooks:Enabled"] = "true"
            })
            .Build();

        var callerProvider = new MutableCallerContextProvider { Current = caller };
        var hostEnv = new StubHostEnvironment
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = resolvedAnchorDir
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostEnvironment>(hostEnv);
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(gatewayOptions));
        services.AddSingleton<IOptionsMonitor<GatewayOptions>>(new StaticOptionsMonitor<GatewayOptions>(gatewayOptions));
        services.AddSingleton<IEpochValidationService>(new FakeEpochValidationService());
        services.AddSingleton<ICallerContextProvider>(callerProvider);

        services.AddAutherisGovernance(gatewayOptions);
        services.AddSingleton<IKeyVaultSecretProvider>(new StubKeyVaultSecretProvider());

        var sp = services.BuildServiceProvider();
        var store = new GovernanceTestStore(
            engine,
            provider,
            connectionString,
            sp,
            callerProvider,
            resolvedAnchorDir,
            ownsAnchorDir,
            isAvailable: true);

        try
        {
            if (runMigrations)
            {
                await store.MigrationRunner.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (initializeStore)
            {
                await store.StoreInitializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<GovernanceTestStore> CreateAdditionalStoreAsync(
        Action<GatewayOptions>? configure = null,
        CallerContext? caller = null,
        bool runMigrations = false,
        bool initializeStore = true,
        bool seedDemoData = false) =>
        CreateFromConnectionStringAsync(
            Provider,
            ConnectionString,
            runMigrations: runMigrations,
            initializeStore: initializeStore,
            seedDemoData: seedDemoData,
            configure: configure,
            caller: caller,
            anchorDir: _anchorDirectory);

    public async Task<string> CreateEmptyDatabaseConnectionStringAsync(string? prefix = null)
    {
        if (IsSqlServer)
        {
            var dbName = GovernanceSqlServerDatabaseName.Sanitize(prefix ?? "empty");
            var masterBuilder = new SqlConnectionStringBuilder(ConnectionString)
            {
                InitialCatalog = "master",
                TrustServerCertificate = true
            };
            await using (var conn = new SqlConnection(masterBuilder.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE [{dbName}]";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var targetBuilder = new SqlConnectionStringBuilder(ConnectionString)
            {
                InitialCatalog = dbName,
                TrustServerCertificate = true
            };
            return targetBuilder.ConnectionString;
        }
        else
        {
            var dbName = GovernancePostgresDatabaseName.Sanitize(prefix ?? "empty");
            var adminBuilder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = "postgres"
            };
            await using (var conn = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var targetBuilder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = dbName
            };
            return targetBuilder.ConnectionString;
        }
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        DbConnection conn = IsSqlServer
            ? new SqlConnection(ConnectionString)
            : new NpgsqlConnection(ConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> ExecuteNonQueryAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<object?> ExecuteScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    }

    public async Task GrantGovernanceAdminAsync(Sid sid)
    {
        const string roleId = "00000000-0000-0000-0000-000000000a01";
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (IsSqlServer)
        {
            await ExecuteNonQueryAsync(
                @"IF NOT EXISTS (SELECT 1 FROM ROLES WHERE id = @roleId OR name = 'GovernanceAdmin')
                  INSERT INTO ROLES (id, name, description, source, is_critical, created_at)
                  VALUES (@roleId, 'GovernanceAdmin', 'Governance Admin', 'MANUAL', 0, @now);",
                ("@roleId", roleId),
                ("@now", now)).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                @"INSERT INTO SUBJECT_ROLE_ASSIGNMENTS (id, subject_sid, role_id, assigned_by, assigned_at)
                  SELECT @id, @sid, r.id, 'test', @now FROM ROLES r WHERE r.name = 'GovernanceAdmin'
                  AND NOT EXISTS (SELECT 1 FROM SUBJECT_ROLE_ASSIGNMENTS sra WHERE sra.subject_sid = @sid AND sra.role_id = r.id);",
                ("@id", Guid.NewGuid().ToString()),
                ("@sid", sid.Value),
                ("@now", now)).ConfigureAwait(false);
        }
        else
        {
            await ExecuteNonQueryAsync(
                @"INSERT INTO ROLES (id, name, description, source, is_critical, created_at)
                  VALUES (@roleId, 'GovernanceAdmin', 'Governance Admin', 'MANUAL', 0, @now)
                  ON CONFLICT (name) DO NOTHING;",
                ("@roleId", roleId),
                ("@now", now)).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                @"INSERT INTO SUBJECT_ROLE_ASSIGNMENTS (id, subject_sid, role_id, assigned_by, assigned_at)
                  SELECT @id, @sid, id, 'test', @now FROM ROLES WHERE name = 'GovernanceAdmin'
                  ON CONFLICT (subject_sid, role_id) DO NOTHING;",
                ("@id", Guid.NewGuid().ToString()),
                ("@sid", sid.Value),
                ("@now", now)).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsAnchorDirectory && Directory.Exists(_anchorDirectory))
        {
            try
            {
                Directory.Delete(_anchorDirectory, recursive: true);
            }
            catch
            {
                // Best-effort temp directory cleanup.
            }
        }
    }

    private static GovernanceTestStore CreateUnavailable(string engine, string provider) =>
        new(
            engine,
            provider,
            string.Empty,
            serviceProvider: null,
            new MutableCallerContextProvider(),
            string.Empty,
            ownsAnchorDirectory: false,
            isAvailable: false);

    private sealed class MutableCallerContextProvider : ICallerContextProvider
    {
        public CallerContext? Current { get; set; }
        public CallerContext? ResolveCallerContext() => Current;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubKeyVaultSecretProvider : IKeyVaultSecretProvider
    {
        private static readonly byte[] KeyBytes = new byte[32];
        public byte[] GetSecretBytes(string secretName) => (byte[])KeyBytes.Clone();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

/// <summary>
/// Abstract base class for engine-agnostic governance repository contract suites.
/// Concrete subclasses bind either <see cref="GovernancePostgresFixture"/> or <see cref="GovernanceSqlServerFixture"/>.
/// </summary>
public abstract class GovernanceContractBase : IAsyncLifetime
{
    protected GovernanceTestStore Store { get; private set; } = null!;

    protected abstract Task<GovernanceTestStore> CreateStoreAsync();

    public virtual async Task InitializeAsync()
    {
        Store = await CreateStoreAsync().ConfigureAwait(false);
    }

    public virtual async Task DisposeAsync()
    {
        if (Store is not null)
        {
            await Store.DisposeAsync().ConfigureAwait(false);
        }
    }

    public abstract Task Reads_never_return_other_tenant_rows();
    public abstract Task Writes_cannot_modify_other_tenant_rows();
    public abstract Task Missing_or_unparseable_tenant_id_throws();
    public abstract Task Upsert_is_idempotent();
    public abstract Task Unique_violation_maps_to_conflict_exception();
    public abstract Task Paging_is_stable_and_ordered();
    public abstract Task Page_size_above_cap_is_clamped_or_rejected();
    public abstract Task Soft_deleted_rows_are_invisible_to_reads();
}
