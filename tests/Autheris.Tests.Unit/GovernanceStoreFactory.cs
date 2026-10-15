using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Autheris.Tests.Unit;

/// <summary>
/// Creates governance repositories whose schema is migrated and initialized, the way the startup hosted service does it
/// (WP-ARC-B-12: repository constructors no longer run DDL). Use instead of <c>new SqliteGovernanceRepository(...)</c>.
/// </summary>
public static class GovernanceStoreFactory
{
    public static SqliteGovernanceRepository CreateSqlite(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions>? options = null,
        IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditChainAnchorStore? auditAnchorStore = null,
        ILogger<SqliteGovernanceRepository>? logger = null,
        IAuditAnchorSigner? auditAnchorSigner = null,
        ICallerContextProvider? callerContextProvider = null,
        IGatewayStatePaths? statePaths = null)
    {
        var repository = CreateUnmigratedSqlite(
            epochValidationService, options, environment, secretProvider, auditAnchorStore, logger, auditAnchorSigner, callerContextProvider, statePaths);
        var gateway = options ?? Options.Create(new GatewayOptions());
        try
        {
            var runner = new SqliteGovernanceMigrationRunner(repository, gateway);
            runner.RunAsync().GetAwaiter().GetResult();
            repository.InitializeAsync().GetAwaiter().GetResult();
            return repository;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>A SQLite repository that is neither migrated nor initialized (for readiness and migration tests).</summary>
    public static SqliteGovernanceRepository CreateUnmigratedSqlite(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions>? options = null,
        IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditChainAnchorStore? auditAnchorStore = null,
        ILogger<SqliteGovernanceRepository>? logger = null,
        IAuditAnchorSigner? auditAnchorSigner = null,
        ICallerContextProvider? callerContextProvider = null,
        IGatewayStatePaths? statePaths = null)
    {
        var gateway = options ?? Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=governance_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" }
        });
        var env = environment ?? DefaultEnvironment();
        var paths = statePaths ?? DefaultStatePaths(env);
        var secrets = secretProvider ?? Substitute.For<IKeyVaultSecretProvider>();
        // Outside Development/Test a test must name its anchor store explicitly (no silent stand-in for AU-01/AU-03).
        if (auditAnchorStore == null && !IsDevOrTest(env))
        {
            throw new InvalidOperationException("Pass an explicit auditAnchorStore when the test environment is not Development/Test.");
        }

        var anchorStore = auditAnchorStore ?? AuditChainAnchorStoreFactory.CreateForGovernance(gateway.Value, env, secrets, auditAnchorSigner, logger, paths);
        return new SqliteGovernanceRepository(
            epochValidationService,
            gateway,
            env,
            secrets,
            anchorStore,
            callerContextProvider ?? Substitute.For<ICallerContextProvider>(),
            paths,
            logger);
    }

    /// <summary>A PostgreSQL repository that is neither migrated nor initialized and opens no connection.</summary>
    public static PostgreSqlGovernanceRepository CreateUnmigratedPostgreSql(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null)
    {
        var env = environment ?? DefaultEnvironment();
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        return new PostgreSqlGovernanceRepository(
            epochValidationService,
            options,
            env,
            secrets,
            AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null),
            Substitute.For<ICallerContextProvider>());
    }

    /// <summary>A SQL Server repository that is neither migrated nor initialized and opens no connection.</summary>
    public static SqlServerGovernanceRepository CreateUnmigratedSqlServer(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null)
    {
        var env = environment ?? DefaultEnvironment();
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        return new SqlServerGovernanceRepository(
            epochValidationService,
            options,
            env,
            secrets,
            AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null),
            Substitute.For<ICallerContextProvider>());
    }

    /// <summary>State paths under a per-process temp directory; Development/Test without a connection string use a private memory database.</summary>
    public static IGatewayStatePaths DefaultStatePaths(IHostEnvironment env) =>
        new GatewayStatePaths(Options.Create(new StateOptions { StateDirectory = Path.Combine(Path.GetTempPath(), "autheris-test-state") }), env);

    private static bool IsDevOrTest(IHostEnvironment env) =>
        string.Equals(env.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(env.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase);

    private static IHostEnvironment DefaultEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }
}
