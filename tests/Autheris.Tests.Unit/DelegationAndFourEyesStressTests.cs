using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class DelegationAndFourEyesStressTests : IDisposable
{
    private readonly EpochValidationService _epochService;
    private readonly SqliteGovernanceRepository _repository;

    public DelegationAndFourEyesStressTests()
    {
        _epochService = new EpochValidationService();
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=delegation_4eyes_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            }
        });
        _repository = new SqliteGovernanceRepository(_epochService, options);
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    #region Delegation Vacation & Expiration Timings

    [Fact]
    public async Task Delegation_VacationHandover_OnlyAuthorizesDelegateWithinActiveWindow()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners.First(o => o.IsActive);

        var delegateA = new Sid("S-1-5-21-DELEGATE-WEEK-1");
        var delegateB = new Sid("S-1-5-21-DELEGATE-WEEK-2");

        var now = DateTimeOffset.UtcNow;

        // Delegate A: Week 1 (Active now)
        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            DataOwnerId = primaryOwner.Id,
            DelegateSid = delegateA,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(7),
            Reason = "Vacation coverage Week 1"
        });

        // Delegate B: Week 2 (Future, starts in 7 days)
        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            DataOwnerId = primaryOwner.Id,
            DelegateSid = delegateB,
            ValidFrom = now.AddDays(7),
            ValidTo = now.AddDays(14),
            Reason = "Vacation coverage Week 2"
        });

        // Act & Assert at current time:
        // Delegate A is currently active
        var isAAuthorized = await _repository.IsAuthorizedApproverForTableAsync(table, delegateA);
        isAAuthorized.ShouldBeTrue("Delegate A must be authorized during Week 1.");

        // Delegate B is not yet active
        var isBAuthorized = await _repository.IsAuthorizedApproverForTableAsync(table, delegateB);
        isBAuthorized.ShouldBeFalse("Delegate B must NOT be authorized before Week 2 begins.");
    }

    [Fact]
    public async Task Delegation_WhenOwnerIsInactive_DelegateCannotApprove()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var delegateSid = new Sid("S-1-5-21-DELEGATE-INACTIVE-OWNER");

        // Create an inactive Data Owner
        var inactiveOwnerId = Guid.NewGuid();
        using (var cmd = _repository.Connection.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                VALUES (@id, 'S-1-5-21-INACTIVE-OWNER', 'CORP\\inactive', 'Inactive Owner', 'inactive@corp.local', 0);";
            cmd.Parameters.AddWithValue("@id", inactiveOwnerId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Assign to table
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();
        using (var cmd = _repository.Connection.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                VALUES (@id, @tid, @oid, 'DELEGATE');";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@tid", meta.Table.Id.ToString());
            cmd.Parameters.AddWithValue("@oid", inactiveOwnerId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Delegate from this inactive owner
        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            DataOwnerId = inactiveOwnerId,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Delegation from inactive owner"
        });

        // An inactive owner's direct SID must not be authorized
        var isOwnerAuthorized = await _repository.IsAuthorizedApproverForTableAsync(table, new Sid("S-1-5-21-INACTIVE-OWNER"));
        isOwnerAuthorized.ShouldBeFalse("Inactive owner must not be authorized.");
    }

    #endregion

    #region Vier-Augen-Prinzip Approvals & Rejections

    [Fact]
    public async Task FourEyes_FullLifecycle_StepOneApproval_StepTwoRejection_EndsInRejected()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();
        meta.Table.RequiresFourEyes.ShouldBeTrue();

        var requesterSid = new Sid("S-1-5-21-REQ-LIFECYCLE");
        var approver1 = new Sid("S-1-5-21-APPROVER-A");
        var approver2 = new Sid("S-1-5-21-APPROVER-B");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Lifecycle test with second step rejection",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Step 1: Approver 1 approves
        var step1 = await _repository.ApproveConsentRequestStepAsync(req.Id, approver1);
        step1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        // Step 2: Approver 2 rejects with reason
        var step2 = await _repository.RejectConsentRequestAsync(req.Id, approver2, "Audit committee declined justification");
        step2.Status.ShouldBe("REJECTED");

        // Attempting to approve after rejection must throw
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, approver1);
        });
    }

    [Fact]
    public async Task FourEyes_StepOneRejection_ImmediatelyRejectsRequest()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-REJ1");
        var approver1 = new Sid("S-1-5-21-APPROVER-A");
        var approver2 = new Sid("S-1-5-21-APPROVER-B");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Immediate step 1 rejection",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var rejected = await _repository.RejectConsentRequestAsync(req.Id, approver1, "No business case");
        rejected.Status.ShouldBe("REJECTED");

        // Second approver cannot approve a rejected request
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, approver2);
        });
    }

    [Fact]
    public async Task FourEyes_RequesterIdentifiedByOwnerAccount_CannotApproveViaSid_ByOwnerId()
    {
        // Review R4-4 (rest): the same data owner appears once by e-mail (requester) and once by SID (approver);
        // the string comparison alone would not match, the resolved owner id does.
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();
        var owner = (await _repository.GetDataOwnersForTableAsync(table)).First();
        owner.Email.ShouldNotBeNullOrWhiteSpace();
        owner.Email.ShouldNotBe(owner.AdSid.Value);

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = new Sid(owner.Email),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = owner.Email,
            BusinessJustification = "Owner id based separation of duties",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, owner.AdSid);
        });
    }

    [Fact]
    public async Task FourEyes_SeparationOfDuties_RequesterWhoIsDataOwner_CannotApproveOwnRequest()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        // APPROVER-A is a table owner for finance_table_5
        var ownerRequesterSid = new Sid("S-1-5-21-APPROVER-A");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = ownerRequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = ownerRequesterSid.Value,
            BusinessJustification = "Self request by owner",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Even though APPROVER-A is a valid owner, they cannot approve their own request!
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, ownerRequesterSid);
        });

        ex.Message.ShouldContain("Separation of duties violated");
    }

    #endregion

    #region Concurrency & Stress Conditions

    [Fact]
    public async Task FourEyes_ConcurrentDuplicateApprovals_OnlyOneSucceeds()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-CONCURRENT");
        var approver1 = new Sid("S-1-5-21-APPROVER-A");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Stress testing duplicate approval",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Launch 10 concurrent approval tasks with the EXACT SAME approver
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await _repository.ApproveConsentRequestStepAsync(req.Id, approver1);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);

        // Exactly one task must succeed; the other 9 must fail cleanly
        results.Count(r => r).ShouldBe(1);
        results.Count(r => !r).ShouldBe(9);

        var finalReq = await _repository.GetConsentRequestAsync(req.Id);
        finalReq.ShouldNotBeNull();
        finalReq.Status.ShouldBe("PENDING_SECOND_APPROVAL");
    }

    [Fact]
    public async Task FourEyes_ConcurrentTwoApprovers_BothCompleteSuccessfullyToApproved()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-2APPROVERS");
        var approver1 = new Sid("S-1-5-21-APPROVER-A");
        var approver2 = new Sid("S-1-5-21-APPROVER-B");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Concurrent distinct approvers test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Both distinct approvers approve simultaneously
        var task1 = _repository.ApproveConsentRequestStepAsync(req.Id, approver1);
        var task2 = _repository.ApproveConsentRequestStepAsync(req.Id, approver2);

        await Task.WhenAll(task1, task2);

        var finalReq = await _repository.GetConsentRequestAsync(req.Id);
        finalReq.ShouldNotBeNull();
        finalReq.Status.ShouldBe("APPROVED");
    }

    #endregion
}
