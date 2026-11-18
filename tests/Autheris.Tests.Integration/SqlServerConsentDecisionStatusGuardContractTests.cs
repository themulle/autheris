namespace Autheris.Tests.Integration;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.ConsentRequests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>WP-0 (PLAN-GOV-113) status guard of approve and reject on SQL Server (UPDLOCK/HOLDLOCK read plus guarded UPDATE).</summary>
public sealed class SqlServerConsentDecisionStatusGuardContractTests : ConsentDecisionStatusGuardContractBase
{
    private SqlServerTestDatabase? _db;
    private SqlServerGovernanceRepository? _repo;

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "SqlServer", ConnectionString = _db.ConnectionString, SeedDemoData = false
            }
        });
        _repo = GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
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
        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            SELECT 1;", conn);
        cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@m", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task EnsureTableOwnerAsync(string sid, TableIdentifier table)
    {
        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
            SELECT @oid, @sid, @acc, @name, @mail, 1
            WHERE NOT EXISTS (SELECT 1 FROM DATA_OWNERS WHERE ad_sid = @sid);
            INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
            SELECT @toid, t.id, o.id, 'PRIMARY'
            FROM TABLES t CROSS JOIN DATA_OWNERS o
            WHERE t.source_name = @d AND t.schema_name = @s AND t.table_name = @t AND o.ad_sid = @sid;", conn);
        cmd.Parameters.AddWithValue("@oid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid);
        cmd.Parameters.AddWithValue("@acc", "CORP\\" + sid);
        cmd.Parameters.AddWithValue("@name", "Owner " + sid);
        cmd.Parameters.AddWithValue("@mail", sid + "@corp.example");
        cmd.Parameters.AddWithValue("@toid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@d", table.Domain);
        cmd.Parameters.AddWithValue("@s", table.Schema);
        cmd.Parameters.AddWithValue("@t", table.TableName);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<int> CountApprovalStepsAsync(Guid requestId)
    {
        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id", conn);
        cmd.Parameters.AddWithValue("@id", requestId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    protected override void SetStatusUpdateSeam(Func<DbConnection, DbTransaction, CancellationToken, Task>? seam) =>
        _repo!.ConsentStatusUpdateSeam = seam;
}
