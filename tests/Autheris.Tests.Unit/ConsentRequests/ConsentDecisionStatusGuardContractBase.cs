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

    private async Task<ConsentRequest> NewRequestAsync(bool fourEyes, string? status = null)
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
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(fourEyes ? new[] { "id" } : Array.Empty<string>())
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
    public async Task W6_Approve_StaleExpectedStatus_RollsBackStepAuditAndStatus()
    {
        RequireAvailable();
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

    [RuntimeSkippableFact]
    public async Task W6_Reject_StaleExpectedStatus_RollsBackStepAuditAndStatus()
    {
        RequireAvailable();
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

    [RuntimeSkippableFact]
    public async Task W6_SecondStep_UsesTheLockedStatusAsExpected_AndRollsBackWhenItChanged()
    {
        RequireAvailable();
        var first = await NewApproverAsync();
        var second = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);
        await EnsureTableOwnerAsync(first, req.TableIdentifier);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(first), requireSecondApprover: true)).Status.ShouldBe("PENDING_SECOND_APPROVAL");
        await EnsureTableOwnerAsync(second, req.TableIdentifier);

        SetStatusUpdateSeam(ForceStatus(req.Id, "REJECTED"));
        try
        {
            await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(second), requireSecondApprover: true));
        }
        finally
        {
            SetStatusUpdateSeam(null);
        }

        await AssertNothingPersistedAsync(req.Id, "PENDING_SECOND_APPROVAL", 1);
        // The unchanged request still completes: the guard did not poison it.
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(second), requireSecondApprover: true)).Status.ShouldBe("APPROVED");
    }

    // ---------------- W-9 (outcome of a decision on an already decided request) ----------------

    [RuntimeSkippableFact]
    public async Task W9_ApproveAndReject_OnAnAlreadyDecidedRequest_AnswerTheStatusConflict()
    {
        RequireAvailable();
        var approver = await NewApproverAsync();
        var other = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: false);
        await EnsureTableOwnerAsync(approver, req.TableIdentifier);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver))).Status.ShouldBe("APPROVED");

        await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(other)));
        await Should.ThrowAsync<ConsentRequestStatusConflictException>(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(other), "Too late"));
        await AssertNothingPersistedAsync(req.Id, "APPROVED", 1);
    }

    // ---------------- W-7 (external workflow step keeps working, guard uses the locked status) ----------------

    [RuntimeSkippableFact]
    public async Task W7_ItsmStep_OnPendingExternalApproval_StillApproves_AndStaleStatusRollsBack()
    {
        RequireAvailable();
        var staleReq = await NewRequestAsync(fourEyes: false, status: "PENDING_EXTERNAL_APPROVAL");
        var attr1 = new ExternalDecisionAttribution(
            Guid.NewGuid(), "EXT-1", "APPROVED",
            new AssertedApprover("board-1", ResolvedSid: "S-1-5-21-EXT-BOARD1"), DateTimeOffset.UtcNow);
        SetStatusUpdateSeam(ForceStatus(staleReq.Id, "REJECTED"));
        try
        {
            await Should.ThrowAsync<ConsentRequestStatusConflictException>(
                () => Repo.ApproveExternalConsentRequestStepAsync(staleReq.Id, attr1, new Sid("S-1-5-21-EXT-BOARD1")));
        }
        finally
        {
            SetStatusUpdateSeam(null);
        }

        await AssertNothingPersistedAsync(staleReq.Id, "PENDING_EXTERNAL_APPROVAL", 0);

        var req = await NewRequestAsync(fourEyes: false, status: "PENDING_EXTERNAL_APPROVAL");
        var attr2 = new ExternalDecisionAttribution(
            Guid.NewGuid(), "EXT-2", "APPROVED",
            new AssertedApprover("board-1", ResolvedSid: "S-1-5-21-EXT-BOARD1"), DateTimeOffset.UtcNow);
        var approved = await Repo.ApproveExternalConsentRequestStepAsync(req.Id, attr2, new Sid("S-1-5-21-EXT-BOARD1"));
        approved.Status.ShouldBe("APPROVED");
    }

    [RuntimeSkippableFact]
    public async Task W7_ItsmFourEyesRequest_KeepsItsSecondExternalStep()
    {
        RequireAvailable();
        var req = await NewRequestAsync(fourEyes: true, status: "PENDING_EXTERNAL_APPROVAL");
        var sysId = Guid.NewGuid();
        var attrA = new ExternalDecisionAttribution(
            sysId, "EXT-A", "APPROVED",
            new AssertedApprover("board-a", ResolvedSid: "S-1-5-21-EXT-BOARDA"), DateTimeOffset.UtcNow);
        var attrB = new ExternalDecisionAttribution(
            sysId, "EXT-B", "APPROVED",
            new AssertedApprover("board-b", ResolvedSid: "S-1-5-21-EXT-BOARDB"), DateTimeOffset.UtcNow);

        var step1 = await Repo.ApproveExternalConsentRequestStepAsync(req.Id, attrA, new Sid("S-1-5-21-EXT-BOARDA"), requireSecondApprover: true);
        step1.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        var step2 = await Repo.ApproveExternalConsentRequestStepAsync(req.Id, attrB, new Sid("S-1-5-21-EXT-BOARDB"), requireSecondApprover: true);
        step2.Status.ShouldBe("APPROVED");
    }

    // ---------------- W-6 / W-9 (concurrency) ----------------

    [RuntimeSkippableFact]
    public async Task W6_ConcurrentApproveAndReject_EndInExactlyOneTerminalOutcome()
    {
        RequireAvailable();
        // One owner group per container (#1009 D13): the same owner decides twice concurrently.
        var approver = await NewApproverAsync();
        var rejector = approver;
        var req = await NewRequestAsync(fourEyes: false);

        var results = await Task.WhenAll(
            Attempt<ConsentRequestStatusConflictException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver))),
            Attempt<ConsentRequestStatusConflictException>(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(rejector), "Declined")));

        results.Count(ok => ok).ShouldBe(1, "exactly one terminal outcome (the loser answers ACCESS_REQUEST_NOT_PENDING)");
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBeOneOf("APPROVED", "REJECTED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
    }

    [RuntimeSkippableFact]
    public async Task W9_SameApproverConcurrentFirstStepOnFourEyesTable_RecordsExactlyOneStep()
    {
        RequireAvailable();
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);

        // The loser sees the first step under the lock and is refused for its true reason (four-eyes), not as a status conflict.
        var results = await Task.WhenAll(
            Attempt<InvalidOperationException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver), requireSecondApprover: true), exact: true),
            Attempt<InvalidOperationException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver), requireSecondApprover: true), exact: true));

        results.Count(ok => ok).ShouldBe(1);
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
    }

    [RuntimeSkippableFact]
    public async Task W9_ConcurrentDecisionsOnTheSecondStep_EndInOneTerminalOutcome()
    {
        RequireAvailable();
        var first = await NewApproverAsync();
        var second = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);
        await EnsureTableOwnerAsync(first, req.TableIdentifier);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(first), requireSecondApprover: true)).Status.ShouldBe("PENDING_SECOND_APPROVAL");
        await EnsureTableOwnerAsync(second, req.TableIdentifier);

        var results = await Task.WhenAll(
            Attempt<ConsentRequestStatusConflictException>(() => Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(second), requireSecondApprover: true)),
            Attempt<ConsentRequestStatusConflictException>(() => Repo.RejectConsentRequestAsync(req.Id, new Sid(second), "Declined")));

        results.Count(ok => ok).ShouldBe(1);
        var final = await Repo.GetConsentRequestAsync(req.Id);
        final!.Status.ShouldBeOneOf("APPROVED", "REJECTED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(2);
    }

    // ---------------- Security review: four-eyes on the atomic final approval (row filter / column masking path) ----------------

    private static (ApprovalStep Step, Consent Consent) AtomicApproval(ConsentRequest req, string approver)
    {
        var step = new ApprovalStep
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = req.Id,
            StepNumber = 1, // stale on purpose: the repository assigns the step number under the lock
            ApproverSid = new Sid(approver),
            Decision = "APPROVED",
            DecidedAt = DateTimeOffset.UtcNow
        };
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = req.TableId,
            TableIdentifier = req.TableIdentifier,
            ConsentRequestId = req.Id,
            TenantId = req.TenantId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(req.RequestedGranteeRef),
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = req.RequestedValidTo,
            CreatedBySid = new Sid(approver),
            Description = "atomic approval"
        };
        return (step, consent);
    }

    [RuntimeSkippableFact]
    public async Task FourEyes_AtomicApproval_BySameApproverAfterFirstStep_IsRefused_AndWritesNothing()
    {
        RequireAvailable();
        var approver = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(approver), requireSecondApprover: true)).Status.ShouldBe("PENDING_SECOND_APPROVAL");

        var (step, consent) = AtomicApproval(req, approver);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Repo.ExecuteAtomicApprovalWithConsentAsync(req.Id, step, consent, [], [], requireSecondApprover: true));
        ex.ShouldNotBeOfType<ConsentRequestStatusConflictException>();

        (await Repo.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("PENDING_SECOND_APPROVAL");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
    }

    [RuntimeSkippableFact]
    public async Task FourEyes_AtomicApproval_OnTheFirstStep_IsRefused_AndASecondApproverCompletesIt()
    {
        RequireAvailable();
        var first = await NewApproverAsync();
        var second = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: true);

        var (firstStep, firstConsent) = AtomicApproval(req, first);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Repo.ExecuteAtomicApprovalWithConsentAsync(req.Id, firstStep, firstConsent, [], [], requireSecondApprover: true));
        ex.ShouldNotBeOfType<ConsentRequestStatusConflictException>();
        (await Repo.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("PENDING");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(0);

        await EnsureTableOwnerAsync(first, req.TableIdentifier);
        (await Repo.ApproveConsentRequestStepAsync(req.Id, new Sid(first), requireSecondApprover: true)).Status.ShouldBe("PENDING_SECOND_APPROVAL");
        var (secondStep, secondConsent) = AtomicApproval(req, second);
        await Repo.ExecuteAtomicApprovalWithConsentAsync(req.Id, secondStep, secondConsent, [], [], requireSecondApprover: true);

        (await Repo.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("APPROVED");
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(2);
    }

    [RuntimeSkippableFact]
    public async Task AtomicApproval_ByTheRequester_IsRefused()
    {
        RequireAvailable();
        var req = await NewRequestAsync(fourEyes: false);
        await GrantGovernanceAdminAsync(req.RequesterSid.Value);

        var (step, consent) = AtomicApproval(req, req.RequesterSid.Value);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Repo.ExecuteAtomicApprovalWithConsentAsync(req.Id, step, consent, [], []));
        ex.Message.ShouldContain("Separation of duties");
        (await Repo.GetConsentRequestAsync(req.Id))!.Status.ShouldBe("PENDING");
    }

    [RuntimeSkippableFact]
    [Trait("Category", "Security")]
    public async Task Concurrent_deciders_exactly_one_grant()
    {
        RequireAvailable();
        var approverA = await NewApproverAsync();
        var approverB = await NewApproverAsync();
        var req = await NewRequestAsync(fourEyes: false);

        Task<Autheris.Application.Governance.Requests.DecisionCasResult> RunDeciderAsync(string approverSid) =>
            Repo.TryDecideAsync(
                req.Id,
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

        var results = await Task.WhenAll(RunDeciderAsync(approverA), RunDeciderAsync(approverB));
        results.Count(r => r.Outcome == Autheris.Application.Governance.Requests.DecisionCasOutcome.Applied).ShouldBe(1);
        results.Count(r => r.Outcome == Autheris.Application.Governance.Requests.DecisionCasOutcome.AlreadyDecided).ShouldBe(1);
        (await CountApprovalStepsAsync(req.Id)).ShouldBe(1);
        var active = await Repo.GetActiveConsentsByConsentRequestIdAsync(req.Id, DateTimeOffset.UtcNow);
        active.Count.ShouldBe(1);
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
