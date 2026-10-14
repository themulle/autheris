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
        app.MapPost("/api/governance/classification/transition", async (
            TableClassificationTransitionPayload payload,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            var result = await stateMachine.TransitionAsync(payload.Proposal, payload.Request, ct).ConfigureAwait(false);
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

            var callerSid = httpContext.User.GetUserSid();
            if (callerSid == null || string.IsNullOrWhiteSpace(callerSid.Value.Value))
            {
                return Results.Unauthorized();
            }

            string? resolvedRole = null;
            if (httpContext.User.IsInRole("ComplianceReviewer") || httpContext.User.IsInRole("ComplianceOfficer"))
            {
                resolvedRole = "DATA_GOVERNANCE_REVIEWER";
            }
            else if (httpContext.User.IsInRole("DataOwner") || EndpointSecurity.IsGlobalGovernanceAdmin(httpContext.User))
            {
                resolvedRole = "DATA_OWNER";
            }

            if (resolvedRole == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (resolvedRole == "DATA_OWNER" && !EndpointSecurity.IsGlobalGovernanceAdmin(httpContext.User))
            {
                if (!string.IsNullOrWhiteSpace(proposal.DataOwnerSid) &&
                    !string.Equals(proposal.DataOwnerSid, callerSid.Value.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            var reviewerSid = callerSid.Value;
            var reviewerRole = resolvedRole;

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

            var callerSid = httpContext.User.GetUserSid();
            if (callerSid == null || string.IsNullOrWhiteSpace(callerSid.Value.Value))
            {
                return Results.Unauthorized();
            }

            string? resolvedRole = null;
            if (httpContext.User.IsInRole("ComplianceReviewer") || httpContext.User.IsInRole("ComplianceOfficer"))
            {
                resolvedRole = "DATA_GOVERNANCE_REVIEWER";
            }
            else if (httpContext.User.IsInRole("DataOwner") || EndpointSecurity.IsGlobalGovernanceAdmin(httpContext.User))
            {
                resolvedRole = "DATA_OWNER";
            }

            if (resolvedRole == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (resolvedRole == "DATA_OWNER" && !EndpointSecurity.IsGlobalGovernanceAdmin(httpContext.User))
            {
                if (!string.IsNullOrWhiteSpace(proposal.DataOwnerSid) &&
                    !string.Equals(proposal.DataOwnerSid, callerSid.Value.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            var reviewerSid = callerSid.Value;
            var reviewerRole = resolvedRole;

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
