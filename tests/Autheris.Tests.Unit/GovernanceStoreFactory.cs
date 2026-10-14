using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Tests.Unit;

/// <summary>
/// Creates governance repositories whose schema is migrated and initialized, the way the startup hosted service does it
/// (WP-ARC-B-12: repository constructors no longer run DDL). Use instead of <c>GovernanceStoreFactory.CreateSqlite(...)</c>.
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
        ICallerContextProvider? callerContextProvider = null)
    {
        var repository = GovernanceStoreFactory.CreateSqlite(
            epochValidationService,
            options,
            environment,
            secretProvider,
            auditAnchorStore,
            logger,
            auditAnchorSigner,
            callerContextProvider);
        try
        {
            var runner = new SqliteGovernanceMigrationRunner(repository, options ?? Options.Create(new GatewayOptions()));
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
}
