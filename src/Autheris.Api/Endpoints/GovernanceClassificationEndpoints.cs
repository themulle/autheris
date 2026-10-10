namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
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
        }).RequireAuthorization();

        // 2. Submit Approval / Review Transition (1-Stage or 2-Stage SoD)
        app.MapPost("/api/governance/classification/transition", async (
            TableClassificationTransitionPayload payload,
            IClassificationWorkflowStateMachine stateMachine,
            CancellationToken ct) =>
        {
            var result = await stateMachine.TransitionAsync(payload.Proposal, payload.Request, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization();

        // 3. Inspect Current Active Governance WORM Configuration Hash
        app.MapGet("/api/governance/classification/config-hash", (
            IWormConfigurationAuditService auditService) =>
        {
            return Results.Ok(new
            {
                CurrentConfigHash = auditService.CurrentConfigHash ?? "UNINITIALIZED",
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }).RequireAuthorization();

        return app;
    }
}

public sealed record TableClassificationTransitionPayload(
    TableClassificationProposal Proposal,
    TableClassificationApprovalRequest Request);
