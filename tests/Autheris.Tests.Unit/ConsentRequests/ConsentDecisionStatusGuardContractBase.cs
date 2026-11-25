using System.Data.Common;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Support;
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

    /// <summary>
    /// Makes <paramref name="sid"/> the owner group of the container of <paramref name="table"/> (#1009, D13: one owner group per container;
    /// calling it again for another sid transfers the ownership). The repository authorizes a decider only as that owner group.
    /// </summary>
    protected abstract Task EnsureTableOwnerAsync(string sid, TableIdentifier table);

    private readonly List<string> _approvers = [];
    private readonly List<TableIdentifier> _tables = [];

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

    /// <summary>Reports the test as skipped (never as passed) when the engine's container is unavailable; a run that requires its databases already failed at startup.</summary>
    private void RequireAvailable()
    {
        if (!Available)
        {
            throw new RuntimeSkipException("The database container for this contract is unavailable (no Docker); set CI=true or SQLGATE_REQUIRE_DATABASES=1 to require it.");
        }
    }

    private async Task<string> NewApproverAsync()
    {
        var sid = "S-1-5-21-APPROVER-" + Guid.NewGuid().ToString("N")[..10];
        await GrantGovernanceAdminAsync(sid);
        _approvers.Add(sid);
        foreach (var table in _tables)
        {
            await EnsureTableOwnerAsync(sid, table);
        }

        return sid;
    }

    private async Task<ConsentRequest> NewRequestAsync(string? status = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = new TableIdentifier("Sales", "Crm", "Guard_" + suffix);
        var table = new Table
        {
            Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
            IsActive = true
        };
        var meta = await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }],
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(Array.Empty<string>())
        });
        _tables.Add(id);
        foreach (var approver in _approvers)
        {
            await EnsureTableOwnerAsync(approver, id);
        }

        var requester = "S-1-5-21-REQ-" + suffix;
        return await Repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableId = meta.Table.Id,
            TableIdentifier = id,
            RequesterSid = new Sid(requester),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester,
            BusinessJustification = "WP-0 status guard",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Status = status ?? "PENDING",
            WorkflowMode = string.Equals(status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase)
                ? ConsentWorkflowMode.External
                : ConsentWorkflowMode.Internal
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

    [RuntimeSkippableFact]
    public async Task W6_Reject_StaleExpectedStatus_RollsBackStepAuditAndStatus()
    {
        RequireAvailable();
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync();
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

    // ---------------- W-9 (outcome of a decision on an already decided request) ----------------

    [RuntimeSkippableFact]
    public async Task W9_DecisionAndReject_OnAnAlreadyDecidedRequest_AnswerTheStatusConflict()
    {
        RequireAvailable();
        var approver = await NewApproverAsync();
        var other = await NewApproverAsync();
        var req = await NewRequestAsync();

        (await DecideAsync(req.Id, approver)).Outcome.ShouldBe(Autheris.Application.Governance.Requests.DecisionCasOutcome.Applied);

        (await DecideAsync(req.Id, other)).Outcome.ShouldBe(Autheris.Application.Governance.Requests.DecisionCasOutcome.AlreadyDecided);
        await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(other), "Too late"));
        (await Repo.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("APPROVED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
        (await Repo.GetActiveConsentsByConsentRequestIdAsync(req.Id, DateTimeOffset.UtcNow)).Count.ShouldBe(1);
    }

    // ---------------- W-6 / W-9 (concurrency) ----------------

    [RuntimeSkippableFact]
    public async Task W6_ConcurrentDecisionAndReject_EndInExactlyOneTerminalOutcome()
    {
        RequireAvailable();
        // One owner group per container (#1009 D13): the same owner decides twice concurrently.
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync();

        var results = await Task.WhenAll(
            AttemptDecision(req.Id, approver),
            Attempt<ConsentRequestStatusConflictException>(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(approver), "Declined")));

        results.Count(ok => ok).ShouldBe(1, "exactly one terminal outcome (the loser answers ACCESS_REQUEST_NOT_PENDING)");
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBeOneOf("APPROVED", "REJECTED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
    }

    [RuntimeSkippableFact]
    [Trait("Category", "Security")]
    public async Task Concurrent_deciders_exactly_one_grant()
    {
        RequireAvailable();
        var approverA = await NewApproverAsync();
        var approverB = await NewApproverAsync();
        var req = await NewRequestAsync();

        var results = await Task.WhenAll(DecideAsync(req.Id, approverA), DecideAsync(req.Id, approverB));
        results.Count(r => r.Outcome == Autheris.Application.Governance.Requests.DecisionCasOutcome.Applied).ShouldBe(1);
        results.Count(r => r.Outcome == Autheris.Application.Governance.Requests.DecisionCasOutcome.AlreadyDecided).ShouldBe(1);
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
        var active = await Repo.GetActiveConsentsByConsentRequestIdAsync(req.Id, DateTimeOffset.UtcNow);
        active.Count.ShouldBe(1);
    }

    /// <summary>One approval through the engine's atomic decision (version CAS, single approval step, grant in the same transaction).</summary>
    private Task<Autheris.Application.Governance.Requests.DecisionCasResult> DecideAsync(Guid requestId, string approverSid) =>
        Repo.TryDecideAsync(
            requestId,
            expectedVersion: 1,
            (_, lockedReq) => Task.FromResult(new Autheris.Application.Governance.Requests.PermissionDecisionRecord(
                NewStatus: "APPROVED",
                DecidedBy: new Sid(approverSid),
                DecidedAt: DateTimeOffset.UtcNow,
                Capacity: ApproverCapacity.DataOwner,
                SelfGrant: false,
                StepDecision: "APPROVED",
                Reason: null,
                ApprovedValidTo: lockedReq.RequestedValidTo)),
            async (tx, decision, updatedReq) =>
            {
                await using var insertCmd = tx.Connection.CreateCommand();
                insertCmd.Transaction = tx.Transaction;
                insertCmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, valid_from, valid_to, is_revoked, tenant_id, grant_source, grant_kind, approver_capacity, self_grant)
                                          VALUES (@cid, @tid, @rid, 'ALLOW', @gt, @gs, @gn, @vf, @vt, 0, @tenant, 'CONSENT_REQUEST', 'DATA', @cap, @sg)";
                var consentId = Guid.NewGuid();
                var p = insertCmd.CreateParameter(); p.ParameterName = "@cid"; p.Value = consentId.ToString("D"); insertCmd.Parameters.Add(p);
                var p2 = insertCmd.CreateParameter(); p2.ParameterName = "@tid"; p2.Value = updatedReq.TableId.ToString("D"); insertCmd.Parameters.Add(p2);
                var p3 = insertCmd.CreateParameter(); p3.ParameterName = "@rid"; p3.Value = updatedReq.Id.ToString("D"); insertCmd.Parameters.Add(p3);
                var p4 = insertCmd.CreateParameter(); p4.ParameterName = "@gt"; p4.Value = updatedReq.RequestedGranteeType.ToString(); insertCmd.Parameters.Add(p4);
                var p5 = insertCmd.CreateParameter(); p5.ParameterName = "@gs"; p5.Value = updatedReq.RequestedGranteeRef; insertCmd.Parameters.Add(p5);
                var p6 = insertCmd.CreateParameter(); p6.ParameterName = "@gn"; p6.Value = updatedReq.RequestedGranteeRef.ToUpperInvariant(); insertCmd.Parameters.Add(p6);
                var p7 = insertCmd.CreateParameter(); p7.ParameterName = "@vf"; p7.Value = decision.DecidedAt.ToString("O"); insertCmd.Parameters.Add(p7);
                var p8 = insertCmd.CreateParameter(); p8.ParameterName = "@vt"; p8.Value = decision.ApprovedValidTo.ToString("O"); insertCmd.Parameters.Add(p8);
                var p9 = insertCmd.CreateParameter(); p9.ParameterName = "@tenant"; p9.Value = updatedReq.TenantId.Value; insertCmd.Parameters.Add(p9);
                var p10 = insertCmd.CreateParameter(); p10.ParameterName = "@cap"; p10.Value = decision.Capacity.ToSqlString(); insertCmd.Parameters.Add(p10);
                var p11 = insertCmd.CreateParameter(); p11.ParameterName = "@sg"; p11.Value = false; insertCmd.Parameters.Add(p11);
                await insertCmd.ExecuteNonQueryAsync();
                return [new Autheris.Application.Governance.Requests.GrantRef(consentId, PermissionGrantKind.Data, updatedReq.TableIdentifier)];
            });

    /// <summary>True when the engine decision applied, false when it lost the race (already decided).</summary>
    private async Task<bool> AttemptDecision(Guid requestId, string approverSid)
    {
        try
        {
            return (await DecideAsync(requestId, approverSid)).Outcome == Autheris.Application.Governance.Requests.DecisionCasOutcome.Applied;
        }
        catch (ConsentRequestStatusConflictException)
        {
            return false;
        }
    }

    /// <summary>True when the call succeeded, false when it lost with exactly <typeparamref name="TLoser"/>; any other exception propagates and fails the test.</summary>
    private static async Task<bool> Attempt<TLoser>(Func<Task<ConsentRequest>> action, bool exact = false)
        where TLoser : InvalidOperationException
    {
        try
        {
            await action();
            return true;
        }
        catch (TLoser ex) when (!exact || ex.GetType() == typeof(TLoser))
        {
            return false;
        }
    }
}
