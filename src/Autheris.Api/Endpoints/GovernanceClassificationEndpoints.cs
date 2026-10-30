namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
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
            IClassificationProposalStore store,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var proposal = await engine.ClassifyTableAsync(context, ct).ConfigureAwait(false);

            // Record the proposer so four-eyes approvals can require a different principal.
            var proposerSid = httpContext.User.GetUserSid();
            if (proposerSid != null && !string.IsNullOrWhiteSpace(proposerSid.Value.Value))
            {
                proposal = proposal with { ProposedBySid = proposerSid.Value.Value };
                await store.SaveProposalAsync(proposal, ct).ConfigureAwait(false);
            }

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
                    proposal = await store.GetProposalByIdAsync(proposal.ProposalId, ct).ConfigureAwait(false)
                               ?? proposal with { Status = transitionResult.NewStatus };
                }
            }

            return Results.Ok(proposal);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationProposed);

        // 2. Submit Approval / Review Transition (1-Stage or 2-Stage SoD)
        app.MapPost("/api/governance/classification/transition", async (
            TableClassificationTransitionPayload payload,
            HttpContext httpContext,
            IClassificationProposalStore store,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            // The proposal is always loaded server-side; a client-supplied copy is a forgery attempt.
            if (payload.Proposal != null || payload.Request == null)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The proposal must not be supplied; it is loaded by ID server-side.", httpContext);
            }

            var proposal = await store.GetProposalByIdAsync(payload.Request.ProposalId, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The proposal was not found.", httpContext);
            }

            var identity = ResolveReviewer(httpContext.User, proposal);
            if (identity.Error != null)
            {
                return identity.Error;
            }

            // Reviewer identity and role come from the authenticated principal only; mismatching client values are rejected.
            if (!string.IsNullOrWhiteSpace(payload.Request.ReviewerSid.Value) &&
                !string.Equals(payload.Request.ReviewerSid.Value, identity.Sid.Value, StringComparison.OrdinalIgnoreCase))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "ReviewerSid must not be supplied or must match the authenticated principal.", httpContext);
            }

            if (!string.IsNullOrWhiteSpace(payload.Request.ReviewerRole) &&
                !string.Equals(payload.Request.ReviewerRole, identity.Role, StringComparison.OrdinalIgnoreCase))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "ReviewerRole must not be supplied or must match the role derived from the authenticated principal.", httpContext);
            }

            var request = payload.Request with
            {
                ProposalId = proposal.ProposalId,
                TableIdentifier = proposal.TableIdentifier,
                ReviewerSid = identity.Sid,
                ReviewerRole = identity.Role!
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
            return proposal != null ? Results.Ok(proposal) : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The proposal was not found.");
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
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The proposal was not found.", httpContext);
            }

            var identity = ResolveReviewer(httpContext.User, proposal);
            if (identity.Error != null)
            {
                return identity.Error;
            }

            if (!ClientIdentityMatches(payload.ReviewerSid, payload.ReviewerRole, identity))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "ReviewerSid/ReviewerRole are derived from the authenticated principal and must not be supplied.", httpContext);
            }

            var reviewerSid = identity.Sid;
            var reviewerRole = identity.Role!;

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
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The proposal was not found.", httpContext);
            }

            var identity = ResolveReviewer(httpContext.User, proposal);
            if (identity.Error != null)
            {
                return identity.Error;
            }

            if (!ClientIdentityMatches(payload.ReviewerSid, payload.ReviewerRole, identity))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "ReviewerSid/ReviewerRole are derived from the authenticated principal and must not be supplied.", httpContext);
            }

            var reviewerSid = identity.Sid;
            var reviewerRole = identity.Role!;

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

    private static bool ClientIdentityMatches(string? clientSid, string? clientRole, ReviewerIdentity identity) =>
        (string.IsNullOrWhiteSpace(clientSid) || string.Equals(clientSid, identity.Sid.Value, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(clientRole) || string.Equals(clientRole, identity.Role, StringComparison.OrdinalIgnoreCase));

    private readonly record struct ReviewerIdentity(Sid Sid, string? Role, IResult? Error);

    /// <summary>
    /// Derives the reviewer SID and workflow role from the authenticated principal and the proposal stage.
    /// Data-owner actions require an exact, non-empty <c>DataOwnerSid</c> match (fail closed: an unset owner denies
    /// everyone, including governance admins, who must assign an owner first). Stage 2 requires a compliance role.
    /// </summary>
    private static ReviewerIdentity ResolveReviewer(ClaimsPrincipal user, TableClassificationProposal proposal)
    {
        var callerSid = user.GetUserSid();
        if (callerSid == null || string.IsNullOrWhiteSpace(callerSid.Value.Value))
        {
            return new ReviewerIdentity(default, null, Results.Unauthorized());
        }

        var isCompliance = user.IsInRole("ComplianceReviewer") || user.IsInRole("ComplianceOfficer");
        var isOwnerCapable = user.IsInRole("DataOwner") || EndpointSecurity.IsGlobalGovernanceAdmin(user);
        var inGovernanceStage = string.Equals(proposal.Status, "PENDING_GOVERNANCE_REVIEW", StringComparison.OrdinalIgnoreCase);

        string? role = null;
        if (inGovernanceStage)
        {
            if (isCompliance)
            {
                role = "DATA_GOVERNANCE_REVIEWER";
            }
        }
        else if (isOwnerCapable)
        {
            role = "DATA_OWNER";
        }
        else if (isCompliance)
        {
            // Let the state machine reject with a precise workflow-state error.
            role = "DATA_GOVERNANCE_REVIEWER";
        }

        if (role == null)
        {
            return new ReviewerIdentity(default, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        if (role == "DATA_OWNER" &&
            (string.IsNullOrWhiteSpace(proposal.DataOwnerSid) ||
             !string.Equals(proposal.DataOwnerSid, callerSid.Value.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return new ReviewerIdentity(default, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return new ReviewerIdentity(callerSid.Value, role, null);
    }
}

/// <summary>
/// Transition request. <c>Proposal</c> must be null (the proposal is loaded server-side); reviewer identity fields in
/// <c>Request</c> are derived from the authenticated principal and rejected with 400 when they disagree.
/// </summary>
public sealed record TableClassificationTransitionPayload(
    TableClassificationProposal? Proposal,
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
