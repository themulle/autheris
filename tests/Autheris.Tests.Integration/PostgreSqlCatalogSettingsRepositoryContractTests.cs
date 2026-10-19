namespace Autheris.Tests.Integration;

using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;

/// <summary>Issue #111 persistence contract of dataset tags and the catalog tier on PostgreSQL.</summary>
public sealed class PostgreSqlCatalogSettingsRepositoryContractTests : CatalogSettingsRepositoryContractTestsBase
{
    private PostgreSqlTestDatabase? _db;
    private readonly List<PostgreSqlGovernanceRepository> _repos = [];

    private PostgreSqlGovernanceRepository Create(DatasetVisibility newTableDefault)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false },
            Catalog = new DataCatalogOptions { DefaultVisibility = newTableDefault }
        });
        var repo = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
        _repos.Add(repo);
        return repo;
    }

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        return _db.IsAvailable ? Create(DatasetVisibility.Hidden) : null;
    }

    protected override Task<IGovernanceRepository?> CreateAdditionalRepositoryAsync(DatasetVisibility newTableDefault = DatasetVisibility.Hidden) =>
        Task.FromResult<IGovernanceRepository?>(Create(newTableDefault));

    protected override async Task ExecuteRawAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DisposeRepositoryAsync()
    {
        foreach (var repo in _repos)
        {
            await repo.DisposeAsync();
        }

        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }
}
