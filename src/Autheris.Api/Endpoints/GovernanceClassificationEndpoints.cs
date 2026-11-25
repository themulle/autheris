namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
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

            // Record the proposer so a requester can never decide an own downgrade.
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

                var transitionTenant = EndpointSecurity.GetRequestTenant(httpContext);
                var autoRequest = new TableClassificationApprovalRequest(
                    proposal.ProposalId,
                    proposal.TableIdentifier,
                    new Sid("SYSTEM_AUTO_APPROVER"),
                    "DATA_OWNER",
                    "APPROVE",
                    autoApproveDecisions,
                    OverallComment: "Zero-Touch Low-Risk Auto-Approved by Policy",
                    TenantId: transitionTenant,
                    Caller: httpContext.User);

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
            IGovernanceCapabilities? capabilities = null,
            IGovernanceDomainRepository? domainRepository = null,
            ITableMetadataRepository? tableMetadataRepository = null,
            CancellationToken ct = default) =>
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

            var identity = await ResolveReviewerAsync(httpContext, proposal, capabilities, domainRepository, tableMetadataRepository, ct).ConfigureAwait(false);
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

            var transitionTenant = EndpointSecurity.GetRequestTenant(httpContext);
            var request = payload.Request with
            {
                ProposalId = proposal.ProposalId,
                TableIdentifier = proposal.TableIdentifier,
                ReviewerSid = identity.Sid,
                ReviewerRole = identity.Role!,
                TenantId = transitionTenant,
                Caller = httpContext.User
            };

            var result = await stateMachine.TransitionAsync(proposal, request, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization(GatewayPolicies.AuthenticatedCaller).WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationTransitioned);

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

        // 5. Submit Human Approval (single decision by a Data Governance member or the data owner)
        app.MapPost("/api/governance/classification/proposals/{proposalId:guid}/approve", async (
            Guid proposalId,
            ClassificationApprovalPayload payload,
            HttpContext httpContext,
            IClassificationProposalStore store,
            IClassificationWorkflowStateMachine stateMachine,
            IGovernanceCapabilities? capabilities = null,
            IGovernanceDomainRepository? domainRepository = null,
            ITableMetadataRepository? tableMetadataRepository = null,
            CancellationToken ct = default) =>
        {
            var proposal = await store.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The proposal was not found.", httpContext);
            }

            var identity = await ResolveReviewerAsync(httpContext, proposal, capabilities, domainRepository, tableMetadataRepository, ct).ConfigureAwait(false);
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
            var transitionTenant = EndpointSecurity.GetRequestTenant(httpContext);

            var request = new TableClassificationApprovalRequest(
                proposal.ProposalId,
                proposal.TableIdentifier,
                reviewerSid,
                reviewerRole,
                "APPROVE",
                payload.ColumnDecisions ?? Array.Empty<ColumnClassificationDecision>(),
                OverallComment: payload.OverallComment,
                Justification: payload.Justification,
                StepUpAuthToken: payload.StepUpAuthToken,
                TenantId: transitionTenant,
                Caller: httpContext.User);

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
            IGovernanceCapabilities? capabilities = null,
            IGovernanceDomainRepository? domainRepository = null,
            ITableMetadataRepository? tableMetadataRepository = null,
            CancellationToken ct = default) =>
        {
            var proposal = await store.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The proposal was not found.", httpContext);
            }

            var identity = await ResolveReviewerAsync(httpContext, proposal, capabilities, domainRepository, tableMetadataRepository, ct).ConfigureAwait(false);
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
            var transitionTenant = EndpointSecurity.GetRequestTenant(httpContext);

            var request = new TableClassificationApprovalRequest(
                proposal.ProposalId,
                proposal.TableIdentifier,
                reviewerSid,
                reviewerRole,
                "REJECT",
                Array.Empty<ColumnClassificationDecision>(),
                OverallComment: payload.Reason,
                TenantId: transitionTenant,
                Caller: httpContext.User);

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

    internal readonly record struct ReviewerIdentity(Sid Sid, string? Role, IResult? Error);

    /// <summary>
    /// Derives the reviewer SID and workflow role from the authenticated principal and the proposal stage.
    /// A Data Governance member decides as <c>DATA_GOVERNANCE_REVIEWER</c>; an owner-capable caller decides as <c>DATA_OWNER</c> and
    /// needs an exact, non-empty <c>DataOwnerSid</c> match (fail closed: an unset owner denies every non-governance caller).
    /// </summary>
    internal static async Task<ReviewerIdentity> ResolveReviewerAsync(
        HttpContext httpContext,
        TableClassificationProposal proposal,
        IGovernanceCapabilities? capabilities,
        IGovernanceDomainRepository? domainRepository,
        ITableMetadataRepository? tableMetadataRepository,
        CancellationToken ct)
    {
        var user = httpContext.User;
        var callerSid = user.GetUserSid();
        if (callerSid == null || string.IsNullOrWhiteSpace(callerSid.Value.Value))
        {
            return new ReviewerIdentity(default, null, Results.Unauthorized());
        }

        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var isGovernance = GatewayPolicies.HasRequestRole(httpContext, Autheris.Domain.Security.GatewayRole.GovernanceAdmin) || EndpointSecurity.IsGlobalGovernanceAdmin(user);
        var isOwnerCapable = isGovernance;
        if (!isOwnerCapable && capabilities is not null)
        {
            var containerRef = ContainerDefaults.Unassigned.Container;
            if (tableMetadataRepository is not null && domainRepository is not null)
            {
                var tableMeta = await tableMetadataRepository.GetTableMetadataAsync(proposal.TableIdentifier, ct).ConfigureAwait(false);
                if (tableMeta is not null)
                {
                    var defaults = await domainRepository.GetContainerForTableAsync(tenant, tableMeta.Table.Id, ct).ConfigureAwait(false);
                    if (defaults.IsValid)
                    {
                        containerRef = defaults.Container;
                    }
                }
            }

            var callerCtx = httpContext.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item) &&
                            item is SecurityPrincipalContext
                ? EndpointSecurity.GetSecurityContext(httpContext)
                : SecurityPrincipalContext.FromPrincipal(user, tenant);
            if (callerCtx.ResolvedTenant is null)
            {
                callerCtx = callerCtx with { ResolvedTenant = tenant };
            }

            isOwnerCapable = await capabilities.CanClassifyAsync(callerCtx, containerRef, tenant, ct).ConfigureAwait(false);
        }

        // Single approval (Amendment 2): one Data Governance member or the data owner decides; there is no second stage and no
        // compliance-only reviewer any more (a compliance role without one of these capacities is refused, fail closed).
        string? role = isGovernance ? "DATA_GOVERNANCE_REVIEWER" : isOwnerCapable ? "DATA_OWNER" : null;
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
