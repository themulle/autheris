namespace Autheris.Tests.Integration;

using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>
/// Creates governance repositories with a migrated and initialized schema (WP-ARC-B-12: repository constructors run no DDL).
/// </summary>
internal static class GovernanceStoreFactory
{
    public static PostgreSqlGovernanceRepository CreatePostgreSql(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null,
        IAuditChainAnchorStore? auditAnchorStore = null,
        Microsoft.Extensions.Logging.ILogger<PostgreSqlGovernanceRepository>? logger = null,
        Autheris.Application.Common.Interfaces.IEventBus? eventBus = null)
    {
        var env = environment ?? DefaultEnvironment();
        var secrets = NSubstitute.Substitute.For<IKeyVaultSecretProvider>();
        var repository = new PostgreSqlGovernanceRepository(
            epochValidationService,
            options,
            env,
            secrets,
            auditAnchorStore ?? AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, WormSigner(options.Value), null),
            NSubstitute.Substitute.For<ICallerContextProvider>(),
            logger,
            eventBus);
        try
        {
            new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync().GetAwaiter().GetResult();
            repository.InitializeAsync().GetAwaiter().GetResult();
            return repository;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    private static readonly Lazy<AsymmetricAuditAnchorSigner> TestSigner = new(() =>
    {
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return new AsymmetricAuditAnchorSigner(System.Text.Encoding.UTF8.GetBytes(ec.ExportPkcs8PrivateKeyPem()));
    });

    // A configured WORM anchor directory requires an external signer (as in production); tests share one throwaway key so restarts verify.
    private static AsymmetricAuditAnchorSigner? WormSigner(GatewayOptions options) =>
        string.IsNullOrWhiteSpace(options.Audit.ChainAnchorWormDirectory) ? null : TestSigner.Value;

    public static SqlServerGovernanceRepository CreateSqlServer(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null)
    {
        var env = environment ?? DefaultEnvironment();
        var secrets = NSubstitute.Substitute.For<IKeyVaultSecretProvider>();
        var repository = new SqlServerGovernanceRepository(
            epochValidationService,
            options,
            env,
            secrets,
            AuditChainAnchorStoreFactory.CreateForGovernance(options.Value, env, secrets, null, null),
            NSubstitute.Substitute.For<ICallerContextProvider>());
        try
        {
            new SqlServerGovernanceMigrationRunner(repository, options).RunAsync().GetAwaiter().GetResult();
            repository.InitializeAsync().GetAwaiter().GetResult();
            return repository;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    private static IHostEnvironment DefaultEnvironment()
    {
        var env = NSubstitute.Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }
}
