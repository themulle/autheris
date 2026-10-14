namespace Autheris.Tests.Integration;

using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Creates governance repositories with a migrated and initialized schema (WP-ARC-B-12: repository constructors run no DDL).
/// </summary>
internal static class GovernanceStoreFactory
{
    public static PostgreSqlGovernanceRepository CreatePostgreSql(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null)
    {
        var repository = new PostgreSqlGovernanceRepository(epochValidationService, options, environment);
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

    public static SqlServerGovernanceRepository CreateSqlServer(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null)
    {
        var repository = new SqlServerGovernanceRepository(epochValidationService, options, environment);
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
}
