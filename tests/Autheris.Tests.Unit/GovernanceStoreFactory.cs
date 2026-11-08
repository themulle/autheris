using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
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
/// (WP-ARC-B-12: repository constructors no longer run DDL). Use instead of constructing a repository directly.
/// </summary>
public static class GovernanceStoreFactory
{
    /// <summary>A PostgreSQL repository that is neither migrated nor initialized and opens no connection.</summary>
    public static PostgreSqlGovernanceRepository CreateUnmigratedPostgreSql(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditChainAnchorStore? auditAnchorStore = null)
    {
        var env = environment ?? DefaultEnvironment();
        var secrets = secretProvider ?? Substitute.For<IKeyVaultSecretProvider>();
        return new PostgreSqlGovernanceRepository(
            epochValidationService,
            options,
            env,
            secrets,
            auditAnchorStore ?? AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null),
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

    /// <summary>State paths under a per-process temp directory.</summary>
    public static IGatewayStatePaths DefaultStatePaths(IHostEnvironment env) =>
        new GatewayStatePaths(Options.Create(new StateOptions { StateDirectory = Path.Combine(Path.GetTempPath(), "autheris-test-state") }), env);

    private static IHostEnvironment DefaultEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }
}
