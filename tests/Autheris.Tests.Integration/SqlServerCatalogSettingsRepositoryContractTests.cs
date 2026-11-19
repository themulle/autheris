namespace Autheris.Tests.Integration;

using Autheris.Tests.Common;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>Issue #111 persistence contract of dataset tags and the catalog tier on SQL Server.</summary>
public sealed class SqlServerCatalogSettingsRepositoryContractTests : CatalogSettingsRepositoryContractTestsBase
{
    private SqlServerTestDatabase? _db;
    private readonly List<SqlServerGovernanceRepository> _repos = [];

    private SqlServerGovernanceRepository Create()
    {
        IHostEnvironment env = new TestHostEnvironment("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = _db!.ConnectionString, SeedDemoData = false }
        });
        var repo = GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
        _repos.Add(repo);
        return repo;
    }

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        return _db.IsAvailable ? Create() : null;
    }

    protected override Task<IGovernanceRepository?> CreateAdditionalRepositoryAsync() =>
        Task.FromResult<IGovernanceRepository?>(Create());

    protected override async Task ExecuteRawAsync(string sql)
    {
        await using var conn = new SqlConnection(_db!.ConnectionString);
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
