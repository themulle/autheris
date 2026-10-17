using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>WP-0 status guard on SQLite (the injection seam runs inside the decision transaction).</summary>
public sealed class SqliteConsentDecisionStatusGuardContractTests : ConsentDecisionStatusGuardContractBase
{
    private SqliteGovernanceRepository? _repo;

    protected override Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
        return Task.FromResult<IGovernanceRepository?>(_repo);
    }

    protected override Task DisposeRepositoryAsync()
    {
        _repo?.Dispose();
        return Task.CompletedTask;
    }

    protected override async Task GrantGovernanceAdminAsync(string sid)
    {
        using var cmd = _repo!.RawConnection().CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator');
            INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
            SELECT @m, id, 'User', @sid FROM ROLES WHERE role_name = 'GovernanceAdmin';";
        cmd.Parameters.AddWithValue("@rid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@m", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@sid", sid);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<int> CountApprovalStepsAsync(Guid requestId)
    {
        using var cmd = _repo!.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @id";
        cmd.Parameters.AddWithValue("@id", requestId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    protected override void SetStatusUpdateSeam(Func<DbConnection, DbTransaction, CancellationToken, Task>? seam) =>
        _repo!.ConsentStatusUpdateSeam = seam;
}
