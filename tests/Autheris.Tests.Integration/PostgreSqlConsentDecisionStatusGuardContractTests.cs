namespace Autheris.Tests.Integration;

using Autheris.Tests.Common;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.ConsentRequests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

/// <summary>WP-0 (PLAN-GOV-113) status guard of approve and reject on PostgreSQL (row lock plus guarded UPDATE).</summary>
public sealed class PostgreSqlConsentDecisionStatusGuardContractTests : ConsentDecisionStatusGuardContractBase
{
    private PostgreSqlTestDatabase? _db;
    private PostgreSqlGovernanceRepository? _repo;

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        IHostEnvironment env = new TestHostEnvironment("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql", ConnectionString = _db.ConnectionString, SeedDemoData = false
            }
        });
        _repo = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
        return _repo;
    }

    protected override async Task DisposeRepositoryAsync()
    {
        if (_repo != null)
        {
            await _repo.DisposeAsync();
        }

        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    protected override async Task GrantGovernanceAdminAsync(string sid)
    {
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT 1;", conn);
        cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@m", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task EnsureTableOwnerAsync(string sid, TableIdentifier table)
    {
        // #1009 D13: the owner group of the table's container is the only owner; making sid the owner transfers the ownership.
        var meta = await Repo.GetTableMetadataAsync(table) ?? throw new InvalidOperationException($"Table {table} is not registered.");
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await Autheris.Tests.Integration.Governance.ContainerOwnerSeed.SetOwnerAsync(conn, Autheris.Tests.Common.TestTenants.Default.Value, meta.Table.Id, sid);
    }

    protected override async Task<int> CountApprovalStepsAsync(Guid requestId)
    {
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id", conn);
        cmd.Parameters.AddWithValue("@id", requestId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    protected override void SetStatusUpdateSeam(Func<DbConnection, DbTransaction, CancellationToken, Task>? seam) =>
        _repo!.ConsentStatusUpdateSeam = seam;
}
