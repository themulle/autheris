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
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Requests;

public sealed partial class PermissionRequestEngine
{
    public const string CatalogGrantNotRenewableCode = "CATALOG_GRANT_NOT_RENEWABLE";
    public const string GrantNotRenewableCode = "GRANT_NOT_RENEWABLE";

    /// <summary>
    /// One-action renewal of a Data or Unmask grant by an eligible approver. The new validity is measured from the renewal
    /// time against the renewer's capacity (no chained extension), a fresh justification is mandatory, the grantee and the
    /// original requester can never renew, and the new consent carries a signed <c>Renewed</c> grant-record entry.
    /// Catalog grants are never renewable.
    /// </summary>
    public async Task<GrantRenewalResult> RenewGrantAsync(GrantRenewalCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var renewer = command.Renewer;
        if (renewer is null || !renewer.IsAuthenticated || renewer.TenantClaimInvalid)
        {
            throw new ConsentDecisionException("UNAUTHENTICATED", "Authenticated renewer context is required.", 401);
        }

        if (command.ConsentId == Guid.Empty)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "ConsentId is required.", 422);
        }

        if (_consentRepository is null)
        {
            throw new ConsentDecisionException("UNAVAILABLE", "Grant renewal is unavailable.", 503);
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

        var tenant = renewer.TenantId;
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            throw new ConsentDecisionException("FORBIDDEN", "A valid tenant context is required.", 403);
        }

        var now = _time.GetUtcNow().ToUniversalTime();
        if (command.NewValidTo is not { } requestedTo || requestedTo == default || requestedTo.ToUniversalTime() <= now ||
            requestedTo.ToUniversalTime() >= DateTimeOffset.MaxValue.AddDays(-1))
        {
            throw new ConsentDecisionException(GrantErrorCodes.EndDateRequired, "A finite future UTC end date (validTo) is required.", 422);
        }

        var newValidTo = requestedTo.ToUniversalTime();
        var consent = await _consentRepository.GetConsentByIdAsync(command.ConsentId, ct).ConfigureAwait(false);
        if (consent is null || consent.TenantId != tenant)
        {
            // A catalog grant lives outside CONSENTS; report its non-renewability, but only inside the caller's tenant.
            if (consent is null && _catalogVisibilityRepository is not null &&
                await _catalogVisibilityRepository.GetByIdAsync(tenant, command.ConsentId, ct).ConfigureAwait(false) is not null)
            {
                await RecordRenewalDeniedAsync(renewer, tenant, command.ConsentId, newValidTo, justification, CatalogGrantNotRenewableCode, ct).ConfigureAwait(false);
                throw new ConsentDecisionException(CatalogGrantNotRenewableCode, "Catalog grants are not renewable.", 422);
            }

            throw new ConsentDecisionException("NOT_FOUND", "Grant was not found.", 404);
        }

        if (consent.GrantKind == PermissionGrantKind.Catalog)
        {
            await RecordRenewalDeniedAsync(renewer, tenant, consent.Id, newValidTo, justification, CatalogGrantNotRenewableCode, ct).ConfigureAwait(false);
            throw new ConsentDecisionException(CatalogGrantNotRenewableCode, "Catalog grants are not renewable.", 422);
        }

        if (consent.Effect != ConsentEffect.Allow || consent.IsPatternGrant || consent.GranteeSid is null ||
            consent.GrantKind is not (PermissionGrantKind.Data or PermissionGrantKind.Unmask) ||
            string.IsNullOrWhiteSpace(consent.TableIdentifier.TableName))
        {
            throw new ConsentDecisionException(GrantNotRenewableCode, "This grant cannot be renewed.", 422);
        }

        if (consent.IsRevoked || consent.EffectiveValidTo <= now)
        {
            throw new ConsentDecisionException("GRANT_NOT_ACTIVE", "Only active grants can be renewed.", 409);
        }

        var requestKind = consent.GrantKind == PermissionGrantKind.Unmask ? ConsentRequestKind.Unmask : ConsentRequestKind.Data;
        ConsentRequest? original = consent.ConsentRequestId is { } rid
            ? await _approvalRepository.GetConsentRequestAsync(rid, ct).ConfigureAwait(false)
            : null;

        var probe = new ConsentRequest
        {
            Id = original?.Id ?? consent.Id,
            TableIdentifier = consent.TableIdentifier,
            RequesterSid = original?.RequesterSid ?? consent.CreatedBySid ?? consent.GranteeSid.Value,
            RequesterIdentifiers = original?.RequesterIdentifiers ?? [],
            RequestedGranteeType = consent.GranteeType,
            RequestedGranteeRef = consent.GranteeSid.Value.Value,
            BusinessJustification = justification,
            TenantId = tenant,
            RequestKind = requestKind,
            GrantKind = consent.GrantKind
        };

        try
        {
            var capacity = await EvaluateRenewalWithGrantPolicyAsync(renewer, consent, probe, requestKind, newValidTo, now, ct).ConfigureAwait(false);

            var renewedConsent = new Consent
            {
                Id = Guid.NewGuid(),
                TableId = consent.TableId,
                TableIdentifier = consent.TableIdentifier,
                DataSource = consent.DataSource,
                ColumnSetMode = consent.ColumnSetMode,
                WorkflowMode = consent.WorkflowMode,
                ProfileItemNo = consent.ProfileItemNo,
                ConsentRequestId = consent.ConsentRequestId,
                Effect = ConsentEffect.Allow,
                TenantId = consent.TenantId,
                GranteeType = consent.GranteeType,
                GranteeSid = consent.GranteeSid,
                CreatedBySid = renewer.UserSid,
                ValidFrom = now,
                ValidTo = newValidTo,
                RenewalCount = consent.RenewalCount + 1,
                GrantSource = consent.GrantSource,
                GrantKind = consent.GrantKind,
                ApproverCapacity = capacity,
                SelfGrant = false,
                TargetColumns = consent.TargetColumns,
                ProfileAssignmentId = consent.ProfileAssignmentId,
                Description = consent.Description,
                ColumnRules = consent.ColumnRules,
                RowFilters = consent.RowFilters,
                WriteRules = consent.WriteRules,
                RowScope = consent.RowScope,
                ApprovingOwners = consent.ApprovingOwners,
                ClassificationAccess = consent.ClassificationAccess
            };

            var created = await _consentRepository.CreateConsentAsync(renewedConsent, ct).ConfigureAwait(false);

            if (consent.ConsentRequestId is { } requestId)
            {
                // The superseded consent ends at renewal; a failed end keeps the earlier validity, never widens access.
                await _consentRepository.RevokeSystemConsentAsync(
                    consent.Id, requestId, renewer.UserSid, "Renewed by approver.", ct).ConfigureAwait(false);
            }

            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = now,
                EventType = AuditEventTypes.GrantRenewed,
                ActorSid = renewer.UserSid,
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "RENEWED",
                TraceId = GetTraceId(),
                TenantId = tenant,
                DetailsJson = new PermissionAuditDetails(
                    Actor: renewer.UserSid.Value,
                    Subject: consent.GranteeSid.Value.Value,
                    TargetObject: consent.TableIdentifier.ToString(),
                    ValidFrom: now,
                    ValidTo: newValidTo,
                    Justification: justification,
                    Capacity: capacity,
                    SelfGrant: false,
                    RequestId: consent.ConsentRequestId,
                    ActorKind: PermissionAuditDetails.ResolveActorKind(renewer),
                    GrantKind: consent.GrantKind.ToSqlString()).ToDetailsJson()
            }, ct).ConfigureAwait(false);

            _cacheInvalidator?.InvalidateTenant(tenant.Value);
            if (_policyEpochRepository is not null)
            {
                await _policyEpochRepository.IncrementTableEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            }

            return new GrantRenewalResult(created, consent.Id, capacity);
        }
        catch (ConsentDecisionException ex) when (ex.StatusCode is 403 or 422)
        {
            await RecordRenewalDeniedAsync(renewer, tenant, consent.Id, newValidTo, justification, ex.ErrorCode, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ApproverCapacity> EvaluateRenewalWithGrantPolicyAsync(
        SecurityPrincipalContext renewer,
        Consent consent,
        ConsentRequest probe,
        ConsentRequestKind requestKind,
        DateTimeOffset newValidTo,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var approverSet = await _approverResolver.ResolveAsync(
            consent.TenantId, requestKind, PermissionTarget.ForTable(consent.TableIdentifier), probe.RequesterSid, probe.RequestedGranteeRef, ct).ConfigureAwait(false);
        var capacity = await _approverEligibility.EvaluateAsync(renewer, approverSet, ct).ConfigureAwait(false);
        if (!capacity.HasValue)
        {
            throw new ConsentDecisionException("FORBIDDEN", "Renewer is not an eligible approver for this grant.", 403);
        }

        var sod = await _sodService.CheckPermissionDecisionAsync(consent.TenantId, renewer, probe, ct).ConfigureAwait(false);
        if (!sod.Allowed || sod.Outcome == SodDecisionOutcome.SelfGrant)
        {
            throw new ConsentDecisionException("SOD_VIOLATION", sod.Reason ?? "Separation of duties violated.", 403);
        }

        var policy = await GetTenantPolicyAsync(consent.TenantId, ct).ConfigureAwait(false);
        var protectionClass = await ResolveMaxClassAsync(probe, ct).ConfigureAwait(false);
        var decision = _grantPolicy.Evaluate(new GrantRequest(
            probe.RequestedGranteeType,
            probe.RequestedGranteeRef,
            GrantKind.Renewal,
            newValidTo,
            now,
            Renewer: renewer.UserSid.Value,
            RenewalCount: consent.RenewalCount,
            RequestKind: requestKind,
            Capacity: capacity.Value,
            SelfGrant: false,
            ProtectionClass: protectionClass,
            TenantPolicy: policy,
            Justification: probe.BusinessJustification));
        if (!decision.Allowed)
        {
            throw new ConsentDecisionException(decision.ErrorCode ?? GrantErrorCodes.Indeterminate, decision.Reason, 422);
        }

        return capacity.Value;
    }

    private Task RecordRenewalDeniedAsync(
        SecurityPrincipalContext renewer,
        TenantId tenant,
        Guid consentId,
        DateTimeOffset newValidTo,
        string justification,
        string errorCode,
        CancellationToken ct) =>
        _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.ConsentDenied,
            ActorSid = renewer.UserSid,
            TargetTable = consentId.ToString("D"),
            Decision = "DENY",
            TraceId = GetTraceId(),
            TenantId = tenant,
            DetailsJson = new PermissionAuditDetails(
                Actor: renewer.UserSid.Value,
                Subject: string.Empty,
                TargetObject: consentId.ToString("D"),
                ValidFrom: null,
                ValidTo: newValidTo,
                Justification: justification,
                Capacity: null,
                SelfGrant: false,
                RequestId: null,
                ActorKind: PermissionAuditDetails.ResolveActorKind(renewer),
                ErrorCode: errorCode).ToDetailsJson()
        }, ct);
}
