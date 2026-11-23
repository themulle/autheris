using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Requests;

public sealed partial class PermissionRequestEngine
{
    /// <summary>
    /// Completes an IT-administrator self-grant. Reachable only from <see cref="SelfGrantService"/>; the self-grant flag is
    /// never accepted from a caller. Request creation and decision commit through the same CAS transaction path as regular
    /// decisions, and all rules are re-evaluated from current state inside the transaction.
    /// </summary>
    internal async Task<PermissionDecisionResult> ExecuteSelfGrantAsync(SelfGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var actor = command.Actor;
        if (actor is null || !actor.IsAuthenticated || actor.TenantClaimInvalid || actor.Principal is null)
        {
            throw new ConsentDecisionException("UNAUTHENTICATED", "Authenticated actor context is required.", 401);
        }

        TenantId tenant;
        try
        {
            tenant = ValidateTenant(actor.TenantId);
        }
        catch (ConsentRequestException ex)
        {
            throw new ConsentDecisionException(ex.Code, ex.Message, 422);
        }

        if (command.Kind is not (PermissionKind.Data or PermissionKind.Unmask) || !Enum.IsDefined(command.GranteeType))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Self-grants support Data and Unmask only.", 422);
        }

        if (string.IsNullOrWhiteSpace(command.GranteeRef))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Grantee reference is required.", 422);
        }

        string justification;
        try
        {
            justification = JustificationPolicy.Validate(command.Justification);
        }
        catch (ConsentRequestException ex)
        {
            throw new ConsentDecisionException(ex.Code, ex.Message, 422);
        }

        var now = _time.GetUtcNow().ToUniversalTime();
        var validTo = command.ValidTo.ToUniversalTime();
        if (command.ValidTo == default || validTo <= now || validTo >= DateTimeOffset.MaxValue.AddDays(-1))
        {
            throw new ConsentDecisionException(GrantErrorCodes.EndDateRequired, "A finite future UTC end date (validTo) is required.", 422);
        }

        var requestKind = command.Kind == PermissionKind.Unmask ? ConsentRequestKind.Unmask : ConsentRequestKind.Data;
        var submission = new PermissionRequestSubmission(
            actor.Principal,
            tenant,
            requestKind,
            command.Target,
            command.GranteeType,
            command.GranteeRef.Trim(),
            justification,
            CallerContext: actor);

        (Guid TableId, TableIdentifier Table, IReadOnlyList<string>? Columns, ContainerKind? ContainerKind, Guid? ContainerId, Guid? ProfileId) resolved;
        try
        {
            resolved = await ResolveAndValidateTargetAsync(tenant, actor, submission, ct).ConfigureAwait(false);
        }
        catch (ConsentRequestException ex)
        {
            throw new ConsentDecisionException(ex.Code, ex.Message, 422);
        }

        var draft = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = resolved.TableId,
            TableIdentifier = resolved.Table,
            RequesterSid = actor.UserSid,
            RequestedGranteeType = command.GranteeType,
            RequestedGranteeRef = command.GranteeRef.Trim(),
            BusinessJustification = justification,
            Status = "PENDING",
            RequestedAt = now,
            RequestedValidFrom = now,
            RequestedValidTo = validTo,
            UpdatedAt = now,
            TenantId = tenant,
            RequesterIdentifiers = SeparationOfDutiesService.CollectSubjectIdentifiers(actor.Principal, actor.UserSid.Value).ToList(),
            RequestedWriteRules = [],
            IncludeReadAccess = false,
            Version = 1,
            WorkflowMode = ConsentWorkflowMode.Internal,
            RequestKind = requestKind,
            GrantKind = MapGrantKind(requestKind),
            TargetColumns = resolved.Columns,
            ContainerKind = resolved.ContainerKind,
            ContainerId = resolved.ContainerId
        };

        // Pre-check outside the transaction so a refused self-grant never leaves a pending request behind.
        await EvaluateSelfGrantWithGrantPolicyAsync(actor, draft, validTo, ct).ConfigureAwait(false);

        var created = await _approvalRepository.CreateConsentRequestAsync(draft, ct).ConfigureAwait(false);
        var decisionCommand = new PermissionDecisionCommand(created.Id, created.Version, actor, true, validTo, justification);
        DecisionCasResult cas;
        try
        {
            cas = await _approvalRepository.TryDecideAsync(
                created.Id,
                created.Version,
                async (_, req) => await EvaluateSelfGrantWithGrantPolicyAsync(actor, req, validTo, ct).ConfigureAwait(false),
                async (tx, decision, updated) =>
                    await ApplyDecisionEffectAndAuditAsync(tx, decisionCommand, decision, updated, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteRequestAsync(created.Id).ConfigureAwait(false);
            throw;
        }

        if (cas.Outcome != DecisionCasOutcome.Applied || cas.Request is null)
        {
            await TryDeleteRequestAsync(created.Id).ConfigureAwait(false);
            throw new ConsentDecisionException("REQUEST_ALREADY_DECIDED", "The self-grant request could not be completed.", 409);
        }

        var finalized = cas.Request;
        _cacheInvalidator?.InvalidateTenant(finalized.TenantId.Value);
        if (_policyEpochRepository is not null && !string.IsNullOrWhiteSpace(finalized.TableIdentifier.TableName))
        {
            await _policyEpochRepository.IncrementTableEpochAsync(finalized.TableIdentifier, ct).ConfigureAwait(false);
        }

        var grants = cas.CreatedGrants ?? [];
        return new PermissionDecisionResult(finalized, grants, grants.Count > 0 ? grants[0] : null);
    }

    private async Task TryDeleteRequestAsync(Guid requestId)
    {
        try
        {
            await _approvalRepository.DeleteConsentRequestAsync(requestId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original failure; an orphaned pending request cannot be decided by anyone but the engine.
        }
    }

    /// <summary>
    /// Evaluates the self-grant rules from current state: IT capacity, no owner group, the grant overlapping the actor
    /// (user or group containing the actor), maximum class over table and columns against the tenant ceiling, and the
    /// configured maximum duration. Any unknown or degraded state denies.
    /// </summary>
    private async Task<PermissionDecisionRecord> EvaluateSelfGrantWithGrantPolicyAsync(
        SecurityPrincipalContext actor,
        ConsentRequest req,
        DateTimeOffset validTo,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(actor.TenantId.Value) && actor.TenantId != req.TenantId)
        {
            throw new ConsentDecisionException("FORBIDDEN", "Actor tenant does not match request tenant.", 403);
        }

        if (_capabilities is null || !_capabilities.IsItAdmin(actor.Principal, req.TenantId))
        {
            throw new ConsentDecisionException("FORBIDDEN", "Only IT administrators can use the self-grant path.", 403);
        }

        if (actor.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            GroupResolutionClaims.HasAnyDegradedClaim(actor.Claims) ||
            SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(actor.Principal))
        {
            throw new ConsentDecisionException("FORBIDDEN", "Group resolution is degraded; self-grant denied fail-closed.", 403);
        }

        var target = BuildTargetFromRequest(req);
        var approverSet = await _approverResolver.ResolveAsync(
            req.TenantId, req.RequestKind, target, req.RequesterSid, req.RequestedGranteeRef, ct).ConfigureAwait(false);
        if (approverSet.OwnerGroupAssigned)
        {
            throw new ConsentDecisionException("SELF_GRANT_OWNER_EXISTS", "The container has an owner group; the owner decides.", 403);
        }

        await EnsureGrantOverlapsActorAsync(actor, req, ct).ConfigureAwait(false);
        return await EvaluateSelfGrantRulesAsync(actor, req, validTo, ct).ConfigureAwait(false);
    }

    private async Task EnsureGrantOverlapsActorAsync(SecurityPrincipalContext actor, ConsentRequest req, CancellationToken ct)
    {
        var actorIds = SeparationOfDutiesService.CollectSubjectIdentifiers(actor.Principal, actor.UserSid.Value);
        if (req.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal)
        {
            if (!actorIds.Any(id => SeparationOfDutiesService.IdentifiersOverlap(id, req.RequestedGranteeRef)))
            {
                throw new ConsentDecisionException("NOT_A_SELF_GRANT", "The grantee does not include the acting administrator.", 422);
            }

            return;
        }

        // Group grantee: probe membership with a neutral requester so only the group overlap decides.
        var probe = new ConsentRequest
        {
            Id = req.Id,
            TableIdentifier = req.TableIdentifier,
            RequesterSid = new Sid("self-grant-probe"),
            RequestedGranteeType = req.RequestedGranteeType,
            RequestedGranteeRef = req.RequestedGranteeRef,
            TenantId = req.TenantId,
            RequestKind = req.RequestKind,
            GrantKind = req.GrantKind
        };
        var sod = await _sodService.CheckPermissionDecisionAsync(req.TenantId, actor, probe, ct).ConfigureAwait(false);
        if (sod.Outcome != SodDecisionOutcome.SelfGrant)
        {
            throw new ConsentDecisionException(
                sod.Allowed ? "NOT_A_SELF_GRANT" : "SOD_VIOLATION",
                sod.Allowed ? "The grantee group does not contain the acting administrator." : sod.Reason ?? "Separation of duties violated.",
                sod.Allowed ? 422 : 403);
        }
    }

    /// <summary>Class, ceiling and duration rules shared by the self-grant service and the decision-path self-grant outcome.</summary>
    private async Task<PermissionDecisionRecord> EvaluateSelfGrantRulesAsync(
        SecurityPrincipalContext actor,
        ConsentRequest req,
        DateTimeOffset validTo,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUniversalTime();
        var policy = await GetTenantPolicyAsync(req.TenantId, ct).ConfigureAwait(false);
        var protectionClass = await ResolveMaxClassAsync(req, ct).ConfigureAwait(false);
        var decision = _grantPolicy.Evaluate(new GrantRequest(
            req.RequestedGranteeType,
            req.RequestedGranteeRef,
            GrantKind.Consent,
            validTo,
            now,
            RequestKind: req.RequestKind,
            Capacity: ApproverCapacity.SelfGrant,
            SelfGrant: true,
            ProtectionClass: protectionClass,
            TenantPolicy: policy,
            Justification: req.BusinessJustification));
        if (!decision.Allowed)
        {
            throw new ConsentDecisionException(decision.ErrorCode ?? GrantErrorCodes.Indeterminate, decision.Reason, 422);
        }

        return new PermissionDecisionRecord(
            NewStatus: "APPROVED",
            DecidedBy: actor.UserSid,
            DecidedAt: now,
            Capacity: ApproverCapacity.SelfGrant,
            SelfGrant: true,
            StepDecision: "APPROVED",
            Reason: req.BusinessJustification,
            ApprovedValidTo: validTo);
    }

    /// <summary>Maximum class over the table and all of its columns; unresolvable protection is treated as Confidential or stricter.</summary>
    private async Task<ProtectionClass> ResolveMaxClassAsync(ConsentRequest req, CancellationToken ct)
    {
        if (_protectionProvider is null || string.IsNullOrWhiteSpace(req.TableIdentifier.TableName))
        {
            return ProtectionClass.Confidential;
        }

        var effective = await _protectionProvider.GetAsync(req.TenantId, req.TableIdentifier, ct).ConfigureAwait(false);
        var max = effective.TableClass;
        foreach (var column in effective.Columns.Values)
        {
            if (column.EffectiveClass > max)
            {
                max = column.EffectiveClass;
            }
        }

        return max;
    }

    internal Task NotifySelfGrantAsync(ConsentRequest request, SecurityPrincipalContext actor, CancellationToken ct)
    {
        if (_notificationDispatcher is null)
        {
            return Task.CompletedTask;
        }

        return _notificationDispatcher.DispatchAsync(
            new GovernanceNotificationMessage(
                AuditEventTypes.SelfGrant,
                request.TenantId.Value,
                string.Empty,
                request.RequestedGranteeRef,
                $"IT self-grant for request {request.Id:D} ({PermissionAuditDetails.ResolveActorKind(actor)}).",
                _time.GetUtcNow(),
                ObjectName: FormatTargetObject(request),
                Justification: request.BusinessJustification),
            ct);
    }
}
