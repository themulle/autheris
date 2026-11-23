using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Requests;

public sealed partial class PermissionRequestEngine
{
    public async Task<bool> WithdrawAsync(Guid requestId, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var preContext = SecurityPrincipalContext.FromPrincipal(caller);
        var callerCtx = ValidateAuthenticatedCaller(caller, preContext, preContext.TenantId);

        var req = await _approvalRepository.GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        var isOwner = req is not null
            && req.TenantId == callerCtx.TenantId
            && (req.RequesterSid == callerCtx.UserSid
                || (req.RequesterIdentifiers?.Any(id => string.Equals(id, callerCtx.UserSid.Value, StringComparison.OrdinalIgnoreCase)) ?? false));

        if (req is null || !isOwner)
        {
            // A foreign, cross-tenant or missing request is indistinguishable for the caller.
            throw new ConsentRequestException("NOT_FOUND", $"Consent request '{requestId}' was not found.");
        }

        if (req.Status is not ("PENDING" or "PENDING_SECOND_APPROVAL" or "PENDING_EXTERNAL_APPROVAL"))
        {
            throw new ConsentRequestException("INVALID_STATE", "The consent request is no longer pending.");
        }

        if (!await _approvalRepository.WithdrawConsentRequestAsync(requestId, req.RequesterSid, ct).ConfigureAwait(false))
        {
            throw new ConsentRequestException("INVALID_STATE", "The consent request could not be withdrawn.");
        }

        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.ConsentWithdrawn,
            ActorSid = callerCtx.UserSid,
            TargetTable = FormatTargetObject(req),
            Decision = "WITHDRAWN",
            TraceId = GetTraceId(),
            TenantId = req.TenantId,
            DetailsJson = new PermissionAuditDetails(
                Actor: callerCtx.UserSid.Value,
                Subject: req.RequestedGranteeRef,
                TargetObject: FormatTargetObject(req),
                ValidFrom: req.RequestedValidFrom,
                ValidTo: req.RequestedValidTo,
                Justification: null,
                Capacity: null,
                SelfGrant: false,
                RequestId: req.Id,
                ActorKind: PermissionAuditDetails.ResolveActorKind(callerCtx),
                GrantKind: req.GrantKind.ToSqlString()).ToDetailsJson()
        }, ct).ConfigureAwait(false);

        return true;
    }
}
