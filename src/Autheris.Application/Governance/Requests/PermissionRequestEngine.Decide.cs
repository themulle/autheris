using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Requests;

public sealed partial class PermissionRequestEngine
{
    public async Task<PermissionDecisionResult> DecideAsync(
        PermissionDecisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RequestId == Guid.Empty)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "RequestId is required.", 422);
        }

        if (command.ExpectedVersion <= 0)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "ExpectedVersion must be a positive integer.", 422);
        }

        if (command.Decider is null || !command.Decider.IsAuthenticated || command.Decider.TenantClaimInvalid)
        {
            throw new ConsentDecisionException("UNAUTHENTICATED", "Authenticated decider context is required.", 401);
        }

        string? validatedReason = null;
        if (!command.Approve)
        {
            try
            {
                validatedReason = JustificationPolicy.Validate(command.Reason);
            }
            catch (ConsentRequestException ex)
            {
                throw new ConsentDecisionException(ex.Code, ex.Message, 422);
            }
        }
        else if (!string.IsNullOrWhiteSpace(command.Reason))
        {
            validatedReason = command.Reason.Trim();
        }

        ConsentRequest? inspectedRequest = null;
        PermissionDecisionRecord? evaluated = null;

        try
        {
            var casResult = await _approvalRepository.TryDecideAsync(
                command.RequestId,
                command.ExpectedVersion,
                async (_, req) =>
                {
                    inspectedRequest = req;
                    evaluated = await EvaluateDecisionInTransactionAsync(command, req, validatedReason, ct).ConfigureAwait(false);
                    return evaluated;
                },
                async (tx, decision, updatedReq) =>
                    await ApplyDecisionEffectAndAuditAsync(tx, command, decision, updatedReq, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            if (casResult.Outcome == DecisionCasOutcome.NotFound)
            {
                throw new ConsentDecisionException("NOT_FOUND", $"Permission request '{command.RequestId}' was not found.", 404);
            }

            if (casResult.Outcome == DecisionCasOutcome.AlreadyDecided)
            {
                var actorKind = PermissionAuditDetails.ResolveActorKind(command.Decider);
                var conflictTarget = casResult.Request is not null ? FormatTargetObject(casResult.Request) : command.RequestId.ToString("D");
                var conflictTenant = casResult.Request is not null ? casResult.Request.TenantId : command.Decider.TenantId;
                var validTenant = TenantId.IsReserved(conflictTenant) || string.IsNullOrWhiteSpace(conflictTenant.Value)
                    ? TenantId.Platform
                    : conflictTenant;

                await _auditWriter.RecordRequiredAsync(new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = _time.GetUtcNow().ToUniversalTime(),
                    EventType = AuditEventTypes.PermissionDecisionConflict,
                    ActorSid = command.Decider.UserSid,
                    TargetTable = conflictTarget,
                    Decision = "CONFLICT",
                    TraceId = GetTraceId(),
                    TenantId = validTenant,
                    DetailsJson = new PermissionAuditDetails(
                        Actor: command.Decider.UserSid.Value,
                        Subject: casResult.Request?.RequestedGranteeRef ?? string.Empty,
                        TargetObject: conflictTarget,
                        ValidFrom: casResult.Request?.RequestedValidFrom,
                        ValidTo: casResult.Request?.RequestedValidTo,
                        Justification: validatedReason ?? casResult.Request?.BusinessJustification,
                        Capacity: null,
                        SelfGrant: false,
                        RequestId: command.RequestId,
                        ActorKind: actorKind,
                        GrantKind: casResult.Request?.GrantKind.ToSqlString(),
                        ErrorCode: "REQUEST_ALREADY_DECIDED",
                        ExpectedVersion: command.ExpectedVersion).ToDetailsJson()
                }, ct).ConfigureAwait(false);

                throw new ConsentDecisionException("REQUEST_ALREADY_DECIDED", $"Permission request '{command.RequestId}' has already been decided.", 409);
            }

            var finalized = casResult.Request
                ?? throw new InvalidOperationException("TryDecideAsync applied decision without returning updated request.");
            var grants = casResult.CreatedGrants ?? [];

            await InvalidateCommittedDecisionAsync(
                finalized.TenantId,
                finalized.TableIdentifier,
                finalized.RequestKind == ConsentRequestKind.ProfileAssignment,
                ct).ConfigureAwait(false);

            await InvalidateGrantTablesAfterCommitAsync(finalized, grants, ct).ConfigureAwait(false);

            if (_notificationDispatcher is not null)
            {
                var notifyEvent = string.Equals(finalized.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)
                    ? AuditEventTypes.PermissionApproved
                    : AuditEventTypes.PermissionRejected;
                await _notificationDispatcher.DispatchAsync(
                    new GovernanceNotificationMessage(
                        notifyEvent,
                        finalized.TenantId.Value,
                        string.Empty,
                        finalized.RequestedGranteeRef,
                        $"Permission request {finalized.Id:D} {finalized.Status.ToLowerInvariant()} ({PermissionAuditDetails.ResolveActorKind(command.Decider)}).",
                        finalized.DecidedAt ?? _time.GetUtcNow(),
                        ObjectName: FormatTargetObject(finalized),
                        Justification: validatedReason ?? finalized.BusinessJustification),
                    ct).ConfigureAwait(false);
            }

            if (evaluated is { SelfGrant: true, NewStatus: "APPROVED" })
            {
                await NotifySelfGrantAsync(finalized, command.Decider, ct).ConfigureAwait(false);
            }

            return new PermissionDecisionResult(finalized, grants, grants.Count > 0 ? grants[0] : null);
        }
        catch (ConsentDecisionException ex) when (ex.StatusCode is 403 or 422 && inspectedRequest is not null)
        {
            await RecordDeniedDecisionAttemptAuditAsync(command, inspectedRequest, validatedReason, ex.ErrorCode, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PermissionDecisionRecord> EvaluateDecisionInTransactionAsync(
        PermissionDecisionCommand command,
        ConsentRequest req,
        string? validatedReason,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.Decider.TenantId.Value) && command.Decider.TenantId != req.TenantId)
        {
            throw new ConsentDecisionException("NOT_FOUND", $"Permission request '{req.Id}' was not found.", 404);
        }

        var target = BuildTargetFromRequest(req);
        var approverSet = await _approverResolver.ResolveAsync(
            req.TenantId,
            req.RequestKind,
            target,
            req.RequesterSid,
            req.RequestedGranteeRef,
            ct).ConfigureAwait(false);

        var capacity = await _approverEligibility.EvaluateAsync(command.Decider, approverSet, ct).ConfigureAwait(false);
        if (!capacity.HasValue)
        {
            throw new ConsentDecisionException("FORBIDDEN", "Decider is not eligible to approve or reject this request.", 403);
        }

        var sod = await _sodService.CheckPermissionDecisionAsync(req.TenantId, command.Decider, req, ct).ConfigureAwait(false);
        var selfGrant = sod.Outcome == SodDecisionOutcome.SelfGrant;
        if (selfGrant)
        {
            // The decider belongs to the grantee group: only an IT administrator may complete this, under the self-grant rules.
            if (!command.Approve || capacity.Value != ApproverCapacity.ItAdmin ||
                req.RequestKind is not (ConsentRequestKind.Data or ConsentRequestKind.Unmask))
            {
                throw new ConsentDecisionException("SOD_VIOLATION", sod.Reason ?? "Separation of duties violated.", 403);
            }
        }
        else if (!sod.Allowed)
        {
            throw new ConsentDecisionException("SOD_VIOLATION", sod.Reason ?? "Separation of duties violated.", 403);
        }

        var now = _time.GetUtcNow().ToUniversalTime();
        var effectiveValidFrom = (req.RequestedValidFrom ?? req.RequestedAt).ToUniversalTime();
        if (effectiveValidFrom < now)
        {
            effectiveValidFrom = now;
        }

        var candidateValidTo = command.ValidTo?.ToUniversalTime() ?? req.RequestedValidTo.ToUniversalTime();

        if (!command.Approve)
        {
            return new PermissionDecisionRecord(
                NewStatus: "REJECTED",
                DecidedBy: command.Decider.UserSid,
                DecidedAt: now,
                Capacity: capacity.Value,
                SelfGrant: false,
                StepDecision: "REJECTED",
                Reason: validatedReason,
                ApprovedValidTo: req.RequestedValidTo);
        }

        if (req.RequestKind == ConsentRequestKind.ProfileAssignment && capacity.Value == ApproverCapacity.ItAdmin)
        {
            throw new ConsentDecisionException("FORBIDDEN", "IT administrators cannot approve profile assignment grants.", 403);
        }

        var requiresFiniteEndDate = req.RequestKind is ConsentRequestKind.Data or ConsentRequestKind.Unmask or ConsentRequestKind.ProfileAssignment;
        if (requiresFiniteEndDate && (candidateValidTo == default || candidateValidTo >= DateTimeOffset.MaxValue.AddDays(-1)))
        {
            throw new ConsentDecisionException(GrantErrorCodes.EndDateRequired, "A finite UTC end date (validTo) is required.", 422);
        }

        if (selfGrant)
        {
            var selfRecord = await EvaluateSelfGrantRulesAsync(command.Decider, req, candidateValidTo, ct).ConfigureAwait(false);
            return selfRecord with { Reason = validatedReason };
        }

        var policy = await GetTenantPolicyAsync(req.TenantId, ct).ConfigureAwait(false);
        var protectionClass = ProtectionClass.Confidential;
        if (_protectionProvider is not null && !string.IsNullOrWhiteSpace(req.TableIdentifier.TableName))
        {
            protectionClass = await ResolveMaxClassAsync(req, ct).ConfigureAwait(false);
        }

        var evalValidTo = req.RequestKind == ConsentRequestKind.Catalog && !command.ValidTo.HasValue && req.RequestedValidTo >= DateTimeOffset.MaxValue.AddDays(-1)
            ? (DateTimeOffset?)null
            : candidateValidTo;

        if (evalValidTo.HasValue || requiresFiniteEndDate)
        {
            var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
                req.RequestedGranteeType,
                req.RequestedGranteeRef,
                MapPolicyGrantKind(req.RequestKind),
                evalValidTo,
                now,
                RequestKind: req.RequestKind,
                Capacity: capacity.Value,
                SelfGrant: false,
                ProtectionClass: protectionClass,
                TenantPolicy: policy,
                Justification: req.BusinessJustification));

            if (!grantDecision.Allowed)
            {
                throw new ConsentDecisionException(grantDecision.ErrorCode ?? GrantErrorCodes.Indeterminate, grantDecision.Reason, 422);
            }
        }

        return new PermissionDecisionRecord(
            NewStatus: "APPROVED",
            DecidedBy: command.Decider.UserSid,
            DecidedAt: now,
            Capacity: capacity.Value,
            SelfGrant: false,
            StepDecision: "APPROVED",
            Reason: validatedReason,
            ApprovedValidTo: evalValidTo ?? DateTimeOffset.MaxValue);
    }

    private async Task<IReadOnlyList<GrantRef>> ApplyDecisionEffectAndAuditAsync(
        IGovernanceTransaction tx,
        PermissionDecisionCommand command,
        PermissionDecisionRecord decision,
        ConsentRequest updatedReq,
        CancellationToken ct)
    {
        IReadOnlyList<GrantRef> grants = [];
        var validFrom = (updatedReq.RequestedValidFrom ?? decision.DecidedAt).ToUniversalTime();
        if (validFrom < decision.DecidedAt)
        {
            validFrom = decision.DecidedAt;
        }

        if (string.Equals(decision.StepDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            if (!_appliers.TryGetValue(updatedReq.RequestKind, out var applier))
            {
                throw new ConsentDecisionException("UNSUPPORTED_REQUEST_KIND", $"No applier registered for request kind '{updatedReq.RequestKind}'.", 500);
            }

            grants = await applier.ApplyAsync(
                tx,
                new ApprovedPermission(
                    updatedReq,
                    decision.DecidedBy,
                    decision.Capacity,
                    decision.SelfGrant,
                    decision.DecidedAt,
                    validFrom,
                    decision.ApprovedValidTo,
                    decision.Reason),
                ct).ConfigureAwait(false);
        }

        var eventType = string.Equals(decision.StepDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)
            ? (decision.SelfGrant ? AuditEventTypes.SelfGrant : AuditEventTypes.PermissionApproved)
            : AuditEventTypes.PermissionRejected;
        var actorKind = PermissionAuditDetails.ResolveActorKind(command.Decider);

        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = decision.DecidedAt,
            EventType = eventType,
            ActorSid = decision.DecidedBy,
            TargetTable = FormatTargetObject(updatedReq),
            Decision = decision.StepDecision,
            TraceId = GetTraceId(),
            TenantId = updatedReq.TenantId,
            DetailsJson = new PermissionAuditDetails(
                Actor: decision.DecidedBy.Value,
                Subject: updatedReq.RequestedGranteeRef,
                TargetObject: FormatTargetObject(updatedReq),
                ValidFrom: validFrom,
                ValidTo: decision.ApprovedValidTo,
                Justification: decision.Reason ?? updatedReq.BusinessJustification,
                Capacity: decision.Capacity,
                SelfGrant: decision.SelfGrant,
                RequestId: updatedReq.Id,
                ActorKind: actorKind,
                GrantKind: updatedReq.GrantKind.ToSqlString(),
                ExternalSystemId: command.External?.ExternalSystemId,
                ExternalReference: command.External?.ExternalReference).ToDetailsJson()
        }, ct).ConfigureAwait(false);

        return grants;
    }

    private Task RecordDeniedDecisionAttemptAuditAsync(
        PermissionDecisionCommand command,
        ConsentRequest req,
        string? reason,
        string errorCode,
        CancellationToken ct)
    {
        var actorKind = PermissionAuditDetails.ResolveActorKind(command.Decider);
        return _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = errorCode.StartsWith("SELF_GRANT", StringComparison.Ordinal)
                ? AuditEventTypes.SelfGrantDenied
                : AuditEventTypes.ConsentDenied,
            ActorSid = command.Decider.UserSid,
            TargetTable = FormatTargetObject(req),
            Decision = "DENY",
            TraceId = GetTraceId(),
            TenantId = req.TenantId,
            DetailsJson = new PermissionAuditDetails(
                Actor: command.Decider.UserSid.Value,
                Subject: req.RequestedGranteeRef,
                TargetObject: FormatTargetObject(req),
                ValidFrom: req.RequestedValidFrom,
                ValidTo: command.ValidTo ?? req.RequestedValidTo,
                Justification: reason ?? req.BusinessJustification,
                Capacity: null,
                SelfGrant: false,
                RequestId: req.Id,
                ActorKind: actorKind,
                GrantKind: req.GrantKind.ToSqlString(),
                ErrorCode: errorCode,
                ExpectedVersion: command.ExpectedVersion).ToDetailsJson()
        }, ct);
    }

    private static PermissionTarget BuildTargetFromRequest(ConsentRequest req) =>
        req.RequestKind switch
        {
            ConsentRequestKind.Catalog when req.ContainerKind.HasValue && req.ContainerId.HasValue =>
                PermissionTarget.ForContainer(req.ContainerKind.Value, req.ContainerId.Value),
            ConsentRequestKind.ProfileAssignment when req.ProfileId.HasValue =>
                PermissionTarget.ForProfile(req.ProfileId.Value),
            _ => PermissionTarget.ForTable(req.TableIdentifier, req.TargetColumns)
        };

    /// <summary>
    /// Issue #1044: a decision can create data grants on tables other than the request's own (an access profile spans several
    /// tables and object patterns). The applier advanced their epochs in the decision transaction; after the commit each table is
    /// invalidated, and a pattern grant (no table) publishes a catalog version change through the empty table key.
    /// </summary>
    private async Task InvalidateGrantTablesAfterCommitAsync(ConsentRequest finalized, IReadOnlyList<GrantRef> grants, CancellationToken ct)
    {
        if (_assignmentStore is null || grants.Count == 0)
        {
            return;
        }

        var tables = new HashSet<TableIdentifier>();
        foreach (var grant in grants)
        {
            if (grant.GrantKind != PermissionGrantKind.Data)
            {
                continue;
            }

            if (grant.Table is { } table && !string.IsNullOrWhiteSpace(table.TableName))
            {
                if (!table.Equals(finalized.TableIdentifier))
                {
                    tables.Add(table);
                }
            }
            else if (grant.ContainerKind is null)
            {
                tables.Add(default);
            }
        }

        if (tables.Count > 0)
        {
            await _assignmentStore.InvalidateTablesAfterCommitAsync(tables, ct).ConfigureAwait(false);
        }
    }
}
