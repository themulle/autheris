namespace Autheris.Tests.Integration;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
                Provider = "SqlServer", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false
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
            INSERT INTO ROLES (id, role_name, description)
            SELECT @rid, 'GovernanceAdmin', 'Governance Administrator'
            WHERE NOT EXISTS (SELECT 1 FROM ROLES WHERE role_name = 'GovernanceAdmin');
            INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
            SELECT @m, id, 'User', @sid FROM ROLES WHERE role_name = 'GovernanceAdmin';", conn);
        cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@m", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid);
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
