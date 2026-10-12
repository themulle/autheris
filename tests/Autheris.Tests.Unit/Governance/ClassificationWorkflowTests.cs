namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Governance.Diagnostics;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ClassificationWorkflowTests
{
    [Fact]
    public async Task ProposalStore_SavesAndFiltersPendingProposalsCorrectly()
    {
        var store = new InMemoryClassificationProposalStore();

        var table1 = new TableIdentifier("corp_lake", "finance", "transactions");
        var table2 = new TableIdentifier("corp_lake", "hr", "employees");
        var table3 = new TableIdentifier("corp_lake", "public", "rates");

        var p1 = new TableClassificationProposal(
            Guid.NewGuid(),
            table1,
            DataOwnerSid: "finance_lead",
            Status: "PENDING_OWNER_REVIEW",
            Columns: new List<ColumnClassificationProposal>
            {
                new("amount", "decimal", "L2_INTERNAL", 2, null, "NONE", 1.0, false)
            },
            DateTimeOffset.UtcNow);

        var p2 = new TableClassificationProposal(
            Guid.NewGuid(),
            table2,
            DataOwnerSid: "hr_lead",
            Status: "PENDING_GOVERNANCE_REVIEW",
            Columns: new List<ColumnClassificationProposal>
            {
                new("iban", "varchar", "L3_CONFIDENTIAL", 3, "IBAN", "MASK_IBAN", 0.95, false)
            },
            DateTimeOffset.UtcNow);

        var p3 = new TableClassificationProposal(
            Guid.NewGuid(),
            table3,
            DataOwnerSid: "public_lead",
            Status: "CLASSIFIED",
            Columns: new List<ColumnClassificationProposal>
            {
                new("rate", "decimal", "L1_PUBLIC", 1, null, "NONE", 1.0, false)
            },
            DateTimeOffset.UtcNow);

        await store.SaveProposalAsync(p1);
        await store.SaveProposalAsync(p2);
        await store.SaveProposalAsync(p3);

        // 1. Query pending for DATA_OWNER
        var ownerPending = await store.GetPendingProposalsAsync(reviewerRole: "DATA_OWNER");
        ownerPending.Count.ShouldBe(1);
        ownerPending[0].ProposalId.ShouldBe(p1.ProposalId);

        // 2. Query pending for DATA_GOVERNANCE_REVIEWER
        var govPending = await store.GetPendingProposalsAsync(reviewerRole: "DATA_GOVERNANCE_REVIEWER");
        govPending.Count.ShouldBe(1);
        govPending[0].ProposalId.ShouldBe(p2.ProposalId);

        // 3. Query pending filtered by OwnerSid
        var financeOnly = await store.GetPendingProposalsAsync(ownerSid: new Sid("finance_lead"));
        financeOnly.Count.ShouldBe(1);
        financeOnly[0].TableIdentifier.ShouldBe(table1);

        // 4. Query all pending (excluding CLASSIFIED)
        var allPending = await store.GetPendingProposalsAsync();
        allPending.Count.ShouldBe(2);

        // 5. Query by TableIdentifier
        var byTable = await store.GetProposalByTableAsync(table2);
        byTable.ShouldNotBeNull();
        byTable.ProposalId.ShouldBe(p2.ProposalId);
    }

    [Fact]
    public async Task TwoStageWorkflow_AdvancesFromOwnerToGovernanceReviewerAndSeals()
    {
        var store = new InMemoryClassificationProposalStore();
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            store);

        var table = new TableIdentifier("sales_db", "crm", "customer_contacts");
        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            table,
            DataOwnerSid: "crm_owner_sid",
            Status: "PENDING_OWNER_REVIEW",
            Columns: new List<ColumnClassificationProposal>
            {
                new("email", "varchar", "L3_CONFIDENTIAL", 3, "EMAIL", "MASK_EMAIL", 0.95, false)
            },
            DateTimeOffset.UtcNow);

        await store.SaveProposalAsync(proposal);

        // Stage 1: Data Owner approves -> should transition to PENDING_GOVERNANCE_REVIEW
        var ownerRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            table,
            ReviewerSid: new Sid("crm_owner_sid"),
            ReviewerRole: "DATA_OWNER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("email", "ACCEPT_PROPOSAL", "L3_CONFIDENTIAL", 3, "MASK_EMAIL")
            },
            OverallComment: "Confirmed customer email column.");

        var stage1Result = await stateMachine.TransitionAsync(proposal, ownerRequest);

        stage1Result.Success.ShouldBeTrue();
        stage1Result.NewStatus.ShouldBe("PENDING_GOVERNANCE_REVIEW");
        stage1Result.RequiresSecondStage.ShouldBeTrue();

        // Check proposal store was updated
        var intermediateProposal = await store.GetProposalByIdAsync(proposal.ProposalId);
        intermediateProposal.ShouldNotBeNull();
        intermediateProposal.Status.ShouldBe("PENDING_GOVERNANCE_REVIEW");

        // Stage 2: Compliance Reviewer (different person, SoD compliant) approves -> should seal to CLASSIFIED
        var govRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            table,
            ReviewerSid: new Sid("dpo_reviewer_sid"), // Different from crm_owner_sid
            ReviewerRole: "DATA_GOVERNANCE_REVIEWER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("email", "ACCEPT_PROPOSAL", "L3_CONFIDENTIAL", 3, "MASK_EMAIL")
            },
            OverallComment: "Governance review approved for GDPR compliance.");

        var stage2Result = await stateMachine.TransitionAsync(intermediateProposal, govRequest);

        stage2Result.Success.ShouldBeTrue();
        stage2Result.NewStatus.ShouldBe("CLASSIFIED");

        var finalizedProposal = await store.GetProposalByIdAsync(proposal.ProposalId);
        finalizedProposal.ShouldNotBeNull();
        finalizedProposal.Status.ShouldBe("CLASSIFIED");
    }

    [Fact]
    public async Task TwoStageWorkflow_BlocksSoDSelfApproval()
    {
        var store = new InMemoryClassificationProposalStore();
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            store);

        var table = new TableIdentifier("sales_db", "crm", "customer_contacts");
        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            table,
            DataOwnerSid: "crm_owner_sid",
            Status: "PENDING_GOVERNANCE_REVIEW",
            Columns: new List<ColumnClassificationProposal>
            {
                new("email", "varchar", "L3_CONFIDENTIAL", 3, "EMAIL", "MASK_EMAIL", 0.95, false)
            },
            DateTimeOffset.UtcNow);

        await store.SaveProposalAsync(proposal);

        // Same person (crm_owner_sid) tries to sign off as governance reviewer
        var maliciousRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            table,
            ReviewerSid: new Sid("crm_owner_sid"),
            ReviewerRole: "DATA_GOVERNANCE_REVIEWER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>());

        var result = await stateMachine.TransitionAsync(proposal, maliciousRequest);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("SOD_ANTI_SELF_APPROVAL_VIOLATION");
    }

    [Fact]
    public async Task RejectionWorkflow_TransitionsToRejected()
    {
        var store = new InMemoryClassificationProposalStore();
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            store);

        var table = new TableIdentifier("warehouse", "raw", "temp_import");
        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            table,
            DataOwnerSid: "data_steward",
            Status: "PENDING_OWNER_REVIEW",
            Columns: new List<ColumnClassificationProposal>
            {
                new("col1", "varchar", "L2_INTERNAL", 2, null, "NONE", 0.5, false)
            },
            DateTimeOffset.UtcNow);

        await store.SaveProposalAsync(proposal);

        var rejectRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            table,
            ReviewerSid: new Sid("data_steward"),
            ReviewerRole: "DATA_OWNER",
            Action: "REJECT",
            ColumnDecisions: new List<ColumnClassificationDecision>(),
            OverallComment: "Schema is obsolete, dropping table.");

        var result = await stateMachine.TransitionAsync(proposal, rejectRequest);

        result.Success.ShouldBeTrue();
        result.NewStatus.ShouldBe("REJECTED");

        var updated = await store.GetProposalByIdAsync(proposal.ProposalId);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task UpdateDataOwner_AssignsOwnerAndAdvancesUnclassifiedStatus()
    {
        var store = new InMemoryClassificationProposalStore();
        var table = new TableIdentifier("sales_db", "orders", "raw_orders");

        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            table,
            DataOwnerSid: null,
            Status: "UNCLASSIFIED",
            Columns: new List<ColumnClassificationProposal>(),
            DateTimeOffset.UtcNow);

        await store.SaveProposalAsync(proposal);

        var updated = await store.UpdateDataOwnerAsync(table, "order_steward_sid");
        updated.ShouldBeTrue();

        var refreshed = await store.GetProposalByIdAsync(proposal.ProposalId);
        refreshed.ShouldNotBeNull();
        refreshed.DataOwnerSid.ShouldBe("order_steward_sid");
        refreshed.Status.ShouldBe("PENDING_OWNER_REVIEW");
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
