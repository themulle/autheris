namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// REST endpoints enabling automated scripts (Python, PowerShell, ServiceNow, Airflow)
/// and web cockpits to trigger table classifications, submit reviews, and transition governance proposals.
/// </summary>
public static class GovernanceClassificationEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceClassificationEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. Trigger Table Pre-Classification (Deterministic or AI-assisted)
        app.MapPost("/api/governance/classification/classify-table", async (
            TableClassificationContext context,
            IClassificationEngine engine,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            var proposal = await engine.ClassifyTableAsync(context, ct).ConfigureAwait(false);

            // Check if eligible for Zero-Touch Auto-Approval
            if (stateMachine.CanZeroTouchAutoApprove(proposal))
            {
                var autoApproveDecisions = new List<ColumnClassificationDecision>(proposal.Columns.Count);
                foreach (var col in proposal.Columns)
                {
                    autoApproveDecisions.Add(new ColumnClassificationDecision(
                        col.ColumnName,
                        "ACCEPT_PROPOSAL",
                        col.ProposedSensitivityKey,
                        col.ProposedSensitivityRank,
                        col.ProposedMaskingRule,
                        "Zero-Touch Auto-Approved"));
                }

                var autoRequest = new TableClassificationApprovalRequest(
                    proposal.ProposalId,
                    proposal.TableIdentifier,
                    new Sid("SYSTEM_AUTO_APPROVER"),
                    "DATA_OWNER",
                    "APPROVE",
                    autoApproveDecisions,
                    OverallComment: "Zero-Touch Low-Risk Auto-Approved by Policy");

                var transitionResult = await stateMachine.TransitionAsync(proposal, autoRequest, ct).ConfigureAwait(false);

                if (transitionResult.Success)
                {
                    proposal = proposal with { Status = transitionResult.NewStatus };
                }
            }

            return Results.Ok(proposal);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationProposed);

        // 2. Submit Approval / Review Transition (1-Stage or 2-Stage SoD)
        // SR-H-04: the proposal is loaded by ID from the store and the reviewer identity/role are derived from the
        // authenticated principal. The proposal, ReviewerSid and ReviewerRole in the payload are ignored.
        app.MapPost("/api/governance/classification/transition", async (
            TableClassificationTransitionPayload payload,
            HttpContext httpContext,
            IClassificationProposalStore store,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            if (payload?.Request == null)
            {
                return Results.BadRequest(new { error = "A transition request is required." });
            }

            var proposal = await store.GetProposalByIdAsync(payload.Request.ProposalId, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                return Results.NotFound(new { error = $"Proposal with ID '{payload.Request.ProposalId}' was not found." });
            }

            var denied = TryResolveReviewer(httpContext.User, out var reviewerSid, out var reviewerRole);
            if (denied != null)
            {
                return denied;
            }

            var request = payload.Request with
            {
                ProposalId = proposal.ProposalId,
                TableIdentifier = proposal.TableIdentifier,
                ReviewerSid = reviewerSid,
                ReviewerRole = reviewerRole,
                ColumnDecisions = payload.Request.ColumnDecisions ?? Array.Empty<ColumnClassificationDecision>()
            };

            var result = await stateMachine.TransitionAsync(proposal, request, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationTransitioned);

        // 3. List Pending Proposals for Review Inbox (Filtered by role or owner)
        app.MapGet("/api/governance/classification/proposals/pending", async (
            string? role,
            string? owner,
            IClassificationProposalStore store,
            CancellationToken ct) =>
        {
            Sid? ownerSid = !string.IsNullOrWhiteSpace(owner) ? (Sid?)new Sid(owner) : null;
            var proposals = await store.GetPendingProposalsAsync(role, ownerSid, ct).ConfigureAwait(false);
            return Results.Ok(proposals);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Summarized, AuditEventTypes.ClassificationProposed);

        // 4. Get Proposal Details by Proposal ID
        app.MapGet("/api/governance/classification/proposals/{proposalId:guid}", async (
            Guid proposalId,
            IClassificationProposalStore store,
            CancellationToken ct) =>
        {
            var proposal = await store.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false);
            return proposal != null ? Results.Ok(proposal) : Results.NotFound(new { error = $"Proposal with ID '{proposalId}' was not found." });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.ClassificationProposed);

        // 5. Submit Human Approval (Stage 1 Data Owner or Stage 2 Compliance Reviewer)
        app.MapPost("/api/governance/classification/proposals/{proposalId:guid}/approve", async (
            Guid proposalId,
            ClassificationApprovalPayload payload,
            HttpContext httpContext,
            IClassificationProposalStore store,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            var proposal = await store.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                return Results.NotFound(new { error = $"Proposal with ID '{proposalId}' was not found." });
            }

            var denied = TryResolveReviewer(httpContext.User, out var reviewerSid, out var reviewerRole);
            if (denied != null)
            {
                return denied;
            }

            var request = new TableClassificationApprovalRequest(
                proposal.ProposalId,
                proposal.TableIdentifier,
                reviewerSid,
                reviewerRole,
                "APPROVE",
                payload.ColumnDecisions ?? Array.Empty<ColumnClassificationDecision>(),
                OverallComment: payload.OverallComment,
                Justification: payload.Justification,
                StepUpAuthToken: payload.StepUpAuthToken);

            var result = await stateMachine.TransitionAsync(proposal, request, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationTransitioned);

        // 6. Submit Rejection
        app.MapPost("/api/governance/classification/proposals/{proposalId:guid}/reject", async (
            Guid proposalId,
            ClassificationRejectionPayload payload,
            HttpContext httpContext,
            IClassificationProposalStore store,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            var proposal = await store.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                return Results.NotFound(new { error = $"Proposal with ID '{proposalId}' was not found." });
            }

            var denied = TryResolveReviewer(httpContext.User, out var reviewerSid, out var reviewerRole);
            if (denied != null)
            {
                return denied;
            }

            var request = new TableClassificationApprovalRequest(
                proposal.ProposalId,
                proposal.TableIdentifier,
                reviewerSid,
                reviewerRole,
                "REJECT",
                Array.Empty<ColumnClassificationDecision>(),
                OverallComment: payload.Reason);

            var result = await stateMachine.TransitionAsync(proposal, request, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationTransitioned);

        // 7. Assign Data Owner to Data Object
        app.MapPost("/api/governance/classification/assign-owner", async (
            AssignDataOwnerPayload payload,
            IClassificationProposalStore store,
            CancellationToken ct) =>
        {
            var tableId = new TableIdentifier(payload.Catalog, payload.Schema, payload.Table);
            var updated = await store.UpdateDataOwnerAsync(tableId, payload.DataOwnerSid, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                Table = tableId.ToQualifiedName(),
                AssignedOwnerSid = payload.DataOwnerSid,
                ProposalUpdated = updated
            });
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationTransitioned);

        // 8. Inspect Current Active Governance WORM Configuration Hash
        app.MapGet("/api/governance/classification/config-hash", (
            IWormConfigurationAuditService auditService) =>
        {
            return Results.Ok(new
            {
                CurrentConfigHash = auditService.CurrentConfigHash ?? "UNINITIALIZED",
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.GovernanceConfigHashRead);

        return app;
    }

    /// <summary>
    /// SR-H-04: derives the reviewer identity and workflow role exclusively from the authenticated principal.
    /// Returns a failure result (401/403) when the caller cannot act as a reviewer; the data-owner match and the
    /// stage preconditions are enforced by the workflow state machine.
    /// </summary>
    private static IResult? TryResolveReviewer(ClaimsPrincipal user, out Sid reviewerSid, out string reviewerRole)
    {
        reviewerSid = default;
        reviewerRole = string.Empty;

        var callerSid = user.GetUserSid();
        if (callerSid == null || string.IsNullOrWhiteSpace(callerSid.Value.Value))
        {
            return Results.Unauthorized();
        }

        if (user.IsInRole("ComplianceReviewer") || user.IsInRole("ComplianceOfficer"))
        {
            reviewerRole = "DATA_GOVERNANCE_REVIEWER";
        }
        else if (user.IsInRole("DataOwner") || EndpointSecurity.IsGlobalGovernanceAdmin(user))
        {
            reviewerRole = "DATA_OWNER";
        }
        else
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        reviewerSid = callerSid.Value;
        return null;
    }
}

public sealed record TableClassificationTransitionPayload(
    TableClassificationProposal Proposal,
    TableClassificationApprovalRequest Request);

public sealed record ClassificationApprovalPayload(
    IReadOnlyList<ColumnClassificationDecision>? ColumnDecisions,
    string? ReviewerRole = null,
    string? ReviewerSid = null,
    string? OverallComment = null,
    string? Justification = null,
    string? StepUpAuthToken = null);

public sealed record ClassificationRejectionPayload(
    string Reason,
    string? ReviewerRole = null,
    string? ReviewerSid = null);

public sealed record AssignDataOwnerPayload(
    string Catalog,
    string Schema,
    string Table,
    string DataOwnerSid);
