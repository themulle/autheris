namespace Autheris.Tests.Unit.Governance;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
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
        // Single approval: every open proposal (a legacy one waiting for the retired second stage included) is pending for either decider.
        var ownerPending = await store.GetPendingProposalsAsync(reviewerRole: "DATA_OWNER");
        ownerPending.Select(p => p.ProposalId).ShouldBe([p1.ProposalId, p2.ProposalId], ignoreOrder: true);

        // 2. Query pending for DATA_GOVERNANCE_REVIEWER
        var govPending = await store.GetPendingProposalsAsync(reviewerRole: "DATA_GOVERNANCE_REVIEWER");
        govPending.Select(p => p.ProposalId).ShouldBe([p1.ProposalId, p2.ProposalId], ignoreOrder: true);

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
    public async Task SingleApprovalWorkflow_OwnerDecisionSeals()
    {
        var store = new InMemoryClassificationProposalStore();
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(options),
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

        // Single approval (Amendment 2): the Data Owner's decision seals the proposal; there is no second stage.
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

        var result = await stateMachine.TransitionAsync(proposal, ownerRequest);

        result.Success.ShouldBeTrue();
        result.NewStatus.ShouldBe("CLASSIFIED");

        var finalizedProposal = await store.GetProposalByIdAsync(proposal.ProposalId);
        finalizedProposal.ShouldNotBeNull();
        finalizedProposal.Status.ShouldBe("CLASSIFIED");
        finalizedProposal.ApprovedBySid.ShouldBe("crm_owner_sid");
    }

    [Fact]
    public async Task RejectionWorkflow_TransitionsToRejected()
    {
        var store = new InMemoryClassificationProposalStore();
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(options),
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

}
