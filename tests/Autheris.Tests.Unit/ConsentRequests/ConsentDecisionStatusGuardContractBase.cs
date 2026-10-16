using System.Data.Common;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>
/// WP-0 (PLAN-GOV-113, P-3 part 2, SEC-113-R2-03): the status guard of the approve step and the reject path on a real engine.
/// <c>UPDATE CONSENT_REQUESTS SET status = @st WHERE id = @id AND status = @expected</c> uses the status read under the lock; zero rows
/// roll back the step row, the audit entry and the status and answer <see cref="ConsentRequestStatusConflictException"/>.
/// W-6 (stale expected value through the seam, plus two concurrent callers), W-7 (ITSM step) and W-9 (concurrent approvers).
/// Shared by the SQLite unit tests and the PostgreSQL/SQL Server container tests.
/// </summary>
public abstract class ConsentDecisionStatusGuardContractBase : IAsyncLifetime
{
    protected IGovernanceRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    protected abstract Task<IGovernanceRepository?> CreateRepositoryAsync();

    protected abstract Task DisposeRepositoryAsync();

    /// <summary>Makes <paramref name="sid"/> a member of the GovernanceAdmin role (an authorized approver of every table).</summary>
    protected abstract Task GrantGovernanceAdminAsync(string sid);

    /// <summary>Number of APPROVAL_STEPS rows of the request (the SQLite read model does not populate <c>ApprovalSteps</c>).</summary>
    protected abstract Task<int> CountApprovalStepsAsync(Guid requestId);

    /// <summary>Installs (or clears with null) the repository seam that runs between the lock read and the guarded UPDATE, inside the decision transaction.</summary>
    protected abstract void SetStatusUpdateSeam(Func<DbConnection, DbTransaction, CancellationToken, Task>? seam);

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo != null)
        {
            Repo = repo;
        }
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private async Task<string> NewApproverAsync()
    {
        var sid = "S-1-5-21-APPROVER-" + Guid.NewGuid().ToString("N")[..10];
        await GrantGovernanceAdminAsync(sid);
        return sid;
    }

    private async Task<ConsentRequest> NewRequestAsync(bool fourEyes, string? status = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = new TableIdentifier("Sales", "Crm", "Guard_" + suffix);
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            Sensitivity = "LOW", RequiresFourEyes = fourEyes, IsActive = true
        };
        var meta = await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        });
        var requester = "S-1-5-21-REQ-" + suffix;
        return await Repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = id,
            RequesterSid = new Sid(requester),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester,
            BusinessJustification = "WP-0 status guard",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Status = status ?? "PENDING"
        });
    }

    private static Func<DbConnection, DbTransaction, CancellationToken, Task> ForceStatus(Guid requestId, string status) =>
        async (conn, tx, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE CONSENT_REQUESTS SET status = '{status}' WHERE id = '{requestId}'";
            await cmd.ExecuteNonQueryAsync(ct);
        };

    private async Task AssertNothingPersistedAsync(Guid requestId, string expectedStatus, int expectedSteps)
    {
        var req = await Repo.GetConsentRequestAsync(requestId);
        req.ShouldNotBeNull();
        req.Status.ShouldBe(expectedStatus);
        (await CountApprovalStepsAsync(requestId)).ShouldBe(expectedSteps);
        var audit = await Repo.GetAuditLogEntriesAsync(2000);
        audit.Count(e => e.DetailsJson.Contains(requestId.ToString(), StringComparison.OrdinalIgnoreCase)
                         && e.EventType is "CONSENT_APPROVAL_STEP" or "CONSENT_REQUEST_REJECTED")
            .ShouldBe(expectedSteps, "no audit entry of the rolled-back decision may exist");
    }

    // ---------------- W-6 (seam) ----------------

    [Fact]
    public async Task W6_Approve_StaleExpectedStatus_RollsBackStepAuditAndStatus()
    {
        if (!Available) return;
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: false);
        SetStatusUpdateSeam(ForceStatus(req.Id, "WITHDRAWN"));
        try
        {
            await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver)));
        }
        finally
        {
            SetStatusUpdateSeam(null);
        }

        await AssertNothingPersistedAsync(req.Id, "PENDING", 0);
    }

    [Fact]
    public async Task W6_Reject_StaleExpectedStatus_RollsBackStepAuditAndStatus()
    {
        if (!Available) return;
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: false);
        SetStatusUpdateSeam(ForceStatus(req.Id, "APPROVED"));
        try
        {
            await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(approver), "Not needed"));
        }
        finally
        {
            SetStatusUpdateSeam(null);
        }

        await AssertNothingPersistedAsync(req.Id, "PENDING", 0);
    }

    [Fact]
    public async Task W6_SecondStep_UsesTheLockedStatusAsExpected_AndRollsBackWhenItChanged()
    {
        if (!Available) return;
        var first = await NewApproverAsync();
        var second = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(first))).Status.ShouldBe("PENDING_SECOND_APPROVAL");

        SetStatusUpdateSeam(ForceStatus(req.Id, "REJECTED"));
        try
        {
            await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(second)));
        }
        finally
        {
            SetStatusUpdateSeam(null);
        }

        await AssertNothingPersistedAsync(req.Id, "PENDING_SECOND_APPROVAL", 1);
        // The unchanged request still completes: the guard did not poison it.
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(second))).Status.ShouldBe("APPROVED");
    }

    // ---------------- W-7 (ITSM step keeps working, guard uses the locked status) ----------------

    [Fact]
    public async Task W7_ItsmStep_OnPendingExternalApproval_StillApproves_AndStaleStatusRollsBack()
    {
        if (!Available) return;
        var staleReq = await NewRequestAsync(fourEyes: false, status: "PENDING_EXTERNAL_APPROVAL");
        SetStatusUpdateSeam(ForceStatus(staleReq.Id, "REJECTED"));
        try
        {
            await Should.ThrowAsync<ConsentRequestStatusConflictException>(
                () => Repo.ApproveConsentRequestStepAsync(staleReq.Id, new Sid("ITSM_SNOW:inst:changeboard"), isExternalItsmApproval: true));
        }
        finally
        {
            SetStatusUpdateSeam(null);
        }

        await AssertNothingPersistedAsync(staleReq.Id, "PENDING_EXTERNAL_APPROVAL", 0);

        var req = await NewRequestAsync(fourEyes: false, status: "PENDING_EXTERNAL_APPROVAL");
        var approved = await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid("ITSM_SNOW:inst:changeboard"), isExternalItsmApproval: true);
        approved.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task W7_ItsmFourEyesRequest_KeepsItsSecondExternalStep()
    {
        if (!Available) return;
        var req = await NewRequestAsync(fourEyes: true, status: "PENDING_EXTERNAL_APPROVAL");

        var step1 = await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid("ITSM_SNOW:inst:board-a"), isExternalItsmApproval: true);
        step1.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        var step2 = await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid("ITSM_SNOW:inst:board-b"), isExternalItsmApproval: true);
        step2.Status.ShouldBe("APPROVED");
    }

    // ---------------- W-6 / W-9 (concurrency) ----------------

    [Fact]
    public async Task W6_ConcurrentApproveAndReject_EndInExactlyOneTerminalOutcome()
    {
        if (!Available) return;
        var approver = await NewApproverAsync();
        var rejector = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: false);

        var results = await Task.WhenAll(
            Attempt(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver))),
            Attempt(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(rejector), "Declined")));

        results.Count(ok => ok).ShouldBe(1, "exactly one terminal outcome");
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBeOneOf("APPROVED", "REJECTED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task W9_SameApproverConcurrentFirstStepOnFourEyesTable_RecordsExactlyOneStep()
    {
        if (!Available) return;
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);

        var results = await Task.WhenAll(
            Attempt(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver))),
            Attempt(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver))));

        results.Count(ok => ok).ShouldBe(1);
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task W9_TwoDifferentApproversConcurrentlyOnTheSecondStep_EndInOneTerminalOutcome()
    {
        if (!Available) return;
        var first = await NewApproverAsync();
        var secondA = await NewApproverAsync();
        var secondB = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(first))).Status.ShouldBe("PENDING_SECOND_APPROVAL");

        var results = await Task.WhenAll(
            Attempt(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(secondA))),
            Attempt(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(secondB))));

        results.Count(ok => ok).ShouldBe(1);
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBe("APPROVED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(2);
    }

    private static async Task<bool> Attempt(Func<Task<ConsentRequest>> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
