namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IConsentRequestService"/>: validation, idempotency, tenant resolution, auto-approve and the
/// external approval hand-off, extracted from the GraphQL mutation. Failures surface as <see cref="ConsentRequestException"/>.
/// </summary>
public sealed class ConsentRequestService : IConsentRequestService
{
    private const string Operation = "RequestTableAccess";
    private const int MinJustificationLength = 5;
    private const int MaxJustificationLength = 2000;
    private const int MaxIdempotencyKeyLength = 256;

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentApprovalRepository _approvalRepository;
    private readonly IIdempotencyStore? _idempotencyStore;
    private readonly IOptions<GatewayOptions>? _gatewayOptions;
    private readonly IGrantPolicy _grantPolicy;
    private readonly IGrantValidityPolicy _grantValidityPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IConsentWorkflowConfigRepository? _workflowConfigRepository;
    private readonly IConsentWorkflowRouter? _workflowRouter;
    private readonly IConsentEventOutbox? _eventOutbox;
    private readonly IIdentitySubjectResolver? _subjectResolver;
    private readonly IConsentMaterializationGuard? _materializationGuard;
    private readonly IRowPolicyDependencyIndex? _policyDependencyIndex;

    public ConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentApprovalRepository approvalRepository,
        IIdempotencyStore? idempotencyStore = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IGrantPolicy? grantPolicy = null,
        IIdentitySubjectResolver? subjectResolver = null,
        IConsentMaterializationGuard? materializationGuard = null,
        IRowPolicyDependencyIndex? policyDependencyIndex = null,
        IGrantValidityPolicy? grantValidityPolicy = null,
        TimeProvider? timeProvider = null,
        IConsentWorkflowConfigRepository? workflowConfigRepository = null,
        IConsentWorkflowRouter? workflowRouter = null,
        IConsentEventOutbox? eventOutbox = null)
    {
        _materializationGuard = materializationGuard;
        _policyDependencyIndex = policyDependencyIndex;
        _grantPolicy = grantPolicy ?? new GrantPolicy();
        _grantValidityPolicy = grantValidityPolicy ?? GrantValidityPolicy.CreateDefault();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
        _workflowConfigRepository = workflowConfigRepository ?? (_approvalRepository as IConsentWorkflowConfigRepository);
        _workflowRouter = workflowRouter;
        _eventOutbox = eventOutbox ?? (_approvalRepository as IConsentEventOutbox);
        _idempotencyStore = idempotencyStore;
        _gatewayOptions = gatewayOptions;
        _subjectResolver = subjectResolver;
    }

    public async Task<SubmitConsentRequestResult> SubmitAsync(ConsentRequestSubmission command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var principal = command.Principal;
        EnsurePersonSubject(principal);

        if (_gatewayOptions?.Value.Authentication.EntraId.SelfServiceEnabled == false)
        {
            throw new ConsentRequestException("FORBIDDEN", "Self-service access requests are disabled.");
        }

        // #341: caller text; a blank component or one with a dot or line break is a client error, not a constructor fault.
        if (!TableIdentifier.TryCreate(command.Domain, command.Schema, command.TableName, out var tableId))
        {
            throw Invalid("Domain, Schema and TableName must be valid, non-empty values.");
        }

        var justification = command.Justification;
        if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < MinJustificationLength)
        {
            throw Invalid("A justification of at least 5 characters is required.");
        }

        if (justification.Length > MaxJustificationLength)
        {
            throw Invalid("The justification must not exceed 2000 characters.");
        }

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset resolvedValidTo;
        try
        {
            resolvedValidTo = _grantValidityPolicy.ResolveRequestedValidTo(
                now,
                command.RequestedValidFrom,
                command.RequestedValidTo,
                command.RequestedDuration == null && command.RequestedValidTo == null ? command.DurationDays : null,
                command.RequestedDuration);
        }
        catch (GrantValidityException ex)
        {
            throw new ConsentRequestException(ex.ErrorCode, ex.Message);
        }

        var durationDays = Math.Max(1, (int)Math.Ceiling((resolvedValidTo - (command.RequestedValidFrom ?? now)).TotalDays));

        if (command.RequestWriteOnly == true && (command.RequestedWriteRules == null || command.RequestedWriteRules.Count == 0))
        {
            throw Invalid("A write-only access request requires at least one write rule.");
        }

        var userSid = command.UserSid;
        var isCrossTenantAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

        var writeRulesFingerprint = command.RequestedWriteRules is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(command.RequestedWriteRules)
            : string.Empty;
        var requestFingerprint = IdempotencyFingerprint(
            ResolveTenantId(principal, command.ContextTenant, isCrossTenantAdmin).Value,
            command.Domain, command.Schema, command.TableName, justification.Trim(), durationDays,
            writeRulesFingerprint, command.RequestWriteOnly);
        var existing = await TryGetIdempotentAsync(userSid, command.IdempotencyKey, requestFingerprint, ct).ConfigureAwait(false);
        if (existing != null)
        {
            return existing;
        }

        var meta = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (meta == null)
        {
            // SEC (low): no table enumeration via NOT_FOUND + table name; neutral FORBIDDEN response.
            throw new ConsentRequestException("FORBIDDEN", "Access request for the specified table is not possible.");
        }

        // R-GQL-9: same ClusterAdmin exception as approve, reject and revoke.
        var tenantId = ResolveTenantId(principal, command.ContextTenant, isCrossTenantAdmin);

        // GOV-12: refuse early (no request stored) for a table that cannot carry consents (caller attribute source, A-10).
        if (_materializationGuard != null)
        {
            try
            {
                await _materializationGuard.EnsureMayMaterializeAsync(tenantId, tableId, Array.Empty<ConsentRowFilter>(), ct).ConfigureAwait(false);
            }
            catch (ConsentOnAttributeSourceException ex)
            {
                throw new ConsentRequestException(ex.Code, ex.Message);
            }
        }

        var validatedWriteRules = WriteRuleWorkflowHelper.ValidateRequestedWriteRules(
            meta, command.RequestedWriteRules, tableId, tenantId, _policyDependencyIndex);

        ConsentWorkflowMode workflowMode;
        Guid? externalSystemId;
        if (_workflowRouter != null)
        {
            var resolution = await _workflowRouter.ResolveForTableAsync(tenantId, tableId, ct).ConfigureAwait(false);
            workflowMode = resolution.Mode;
            externalSystemId = workflowMode == ConsentWorkflowMode.External ? resolution.ExternalSystemId : null;
            var routeMaxEnd = (command.RequestedValidFrom ?? now).AddDays(Math.Max(1, resolution.MaxDurationDays));
            if (resolvedValidTo > routeMaxEnd)
            {
                if (command.RequestedValidTo.HasValue || command.RequestedDuration is not null || command.DurationDays > 0)
                {
                    throw new ConsentRequestException(
                        "VALIDITY_EXCEEDS_MAXIMUM",
                        $"Requested grant duration exceeds the configured route maximum of {resolution.MaxDurationDays} days.");
                }

                resolvedValidTo = routeMaxEnd;
            }
        }
        else
        {
            var route = _workflowConfigRepository != null
                ? await _workflowConfigRepository.ResolveWorkflowRouteAsync(tenantId, tableId, ct).ConfigureAwait(false)
                : null;
            workflowMode = route?.Mode ?? ConsentWorkflowMode.Internal;
            externalSystemId = workflowMode == ConsentWorkflowMode.External ? route?.ExternalSystemId : null;
        }

        // a user request is an individual grant; its duration is bounded by the class of the table (fail closed).
        var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
            GranteeType.User,
            userSid.Value,
            GrantKind.Consent,
            resolvedValidTo,
            now,
            GrantClassifier.Classify([meta]),
            Requester: userSid.Value,
            Justification: justification));
        if (!grantDecision.Allowed)
        {
            throw new ConsentRequestException(grantDecision.ErrorCode ?? GrantErrorCodes.Indeterminate, grantDecision.Reason);
        }

        var initialStatus = workflowMode == ConsentWorkflowMode.External
            ? "PENDING_EXTERNAL_APPROVAL"
            : "PENDING";

        string? contextJson = command.Context != null
            ? System.Text.Json.JsonSerializer.Serialize(command.Context)
            : null;

        bool includeReadAccess = validatedWriteRules.Count == 0 || command.RequestWriteOnly == false;
        var request = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = userSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = userSid.Value,
            BusinessJustification = justification.Trim(),
            RequestedValidFrom = command.RequestedValidFrom ?? now,
            RequestedValidTo = resolvedValidTo,
            Status = initialStatus,
            WorkflowMode = workflowMode,
            ExternalSystemId = externalSystemId,
            TenantId = tenantId,
            RequesterIdentifiers = ExtractPrincipalIdentifiers(principal, userSid.Value),
            ContextJson = contextJson,
            RequestedWriteRules = validatedWriteRules,
            IncludeReadAccess = includeReadAccess
        };

        var created = await _approvalRepository.CreateConsentRequestAsync(request, ct).ConfigureAwait(false);
        if (_eventOutbox != null)
        {
            var payload = ConsentEventPayloads.ForRequest(created, now);
            await _eventOutbox.AppendAsync(
                new ConsentEventWrite(
                    created.TenantId.Value,
                    Guid.NewGuid(),
                    ConsentEventTypes.RequestCreated,
                    created.Id.ToString("D"),
                    ConsentEventPayloads.Serialize(payload),
                    now,
                    created.ExternalSystemId),
                transaction: null,
                ct: ct).ConfigureAwait(false);
        }

        if (workflowMode == ConsentWorkflowMode.Internal && _gatewayOptions?.Value.IsAutoApproveEnabled == true)
        {
            await _approvalRepository.ActivateConsentForAutoApproveAsync(created.Id, ct).ConfigureAwait(false);
            var approved = new SubmitConsentRequestResult
            {
                RequestId = created.Id,
                Status = "APPROVED",
                Message = "[INSECURE GETTING STARTED] Consent request auto-approved."
            };
            await StoreIdempotentAsync(userSid, command.IdempotencyKey, requestFingerprint, approved, ct).ConfigureAwait(false);
            return approved;
        }

        var submitted = new SubmitConsentRequestResult
        {
            RequestId = created.Id,
            Status = created.Status,
            Message = "Consent request submitted successfully."
        };
        await StoreIdempotentAsync(userSid, command.IdempotencyKey, requestFingerprint, submitted, ct).ConfigureAwait(false);
        return submitted;
    }

    /// <summary>
    /// R-GQL-9: the tenant of a mutation is the one resolved for the request context (which honours a ClusterAdmin's
    /// X-Tenant-ID). The token claim only serves as a cross-check: a different claim tenant is forbidden, except for
    /// canonical ClusterAdmins. Without a context tenant the claim tenant applies.
    /// </summary>
    public static TenantId ResolveTenantId(ClaimsPrincipal? principal, TenantId contextTenant, bool isCrossTenantAdmin)
    {
        // DT-A-3: claims only, no legacy sentinel. An absent or invalid claim is no cross-check value (the HTTP gate has already
        // rejected such principals); the context tenant decides, and without either the request is unresolved.
        var claim = TenantClaimReader.Read(principal);
        var principalTenant = claim.Kind == ClaimTenantKind.Valid ? (TenantId?)claim.Tenant : null;
        var hasContext = contextTenant.Value is not null;

        if (principalTenant is { } own && hasContext && own != contextTenant && !isCrossTenantAdmin)
        {
            throw new ConsentRequestException("FORBIDDEN", "Cross-tenant access forbidden: The token tenant does not match the tenant of the connection context.");
        }

        if (hasContext)
        {
            return contextTenant;
        }

        return principalTenant ?? throw new Autheris.Domain.Exceptions.TenantUnresolvedException();
    }

    /// <summary>All identifiers (SID, object id, UPN, e-mail, ...) a principal is known by.</summary>
    public static List<string> ExtractPrincipalIdentifiers(ClaimsPrincipal? principal, string? fallbackSid = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(fallbackSid))
        {
            set.Add(fallbackSid.Trim());
        }

        if (principal != null)
        {
            foreach (var claim in principal.Claims)
            {
                if (claim.Type is ClaimTypes.PrimarySid
                                or ClaimTypes.NameIdentifier
                                or "objectSid"
                                or "onprem_sid"
                                or "oid"
                                or "sub"
                                or ClaimTypes.Upn
                                or ClaimTypes.Email
                                or ClaimTypes.Name)
                {
                    if (!string.IsNullOrWhiteSpace(claim.Value))
                    {
                        set.Add(claim.Value.Trim());
                    }
                }
            }
        }

        return set.ToList();
    }

    private static ConsentRequestException Invalid(string message) => new("VALIDATION_ERROR", message);

    // GQL-11: a key only replays the result of the same operation with the same arguments in the same tenant.
    private static string IdempotencyFingerprint(params object?[] parts)
    {
        var material = string.Join('\u001f', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture) ?? string.Empty));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        var trimmed = idempotencyKey.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException($"Idempotency key exceeds maximum allowed length of {MaxIdempotencyKeyLength} characters.", nameof(idempotencyKey));
        }
        return Uri.EscapeDataString(trimmed);
    }

    private async Task<SubmitConsentRequestResult?> TryGetIdempotentAsync(Sid userSid, string? idempotencyKey, string fingerprint, CancellationToken ct)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey == null || _idempotencyStore == null) return null;
        var compositeKey = $"idempotency:{userSid.Value}:{Operation}:{normalizedKey}:{fingerprint}";
        return await _idempotencyStore.GetAsync<SubmitConsentRequestResult>(compositeKey, ct).ConfigureAwait(false);
    }

    private async Task StoreIdempotentAsync(Sid userSid, string? idempotencyKey, string fingerprint, SubmitConsentRequestResult result, CancellationToken ct)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey == null || _idempotencyStore == null) return;
        var compositeKey = $"idempotency:{userSid.Value}:{Operation}:{normalizedKey}:{fingerprint}";
        await _idempotencyStore.SetIfNotExistsAsync(compositeKey, result, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    public async Task<bool> WithdrawAsync(Guid requestId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        EnsurePersonSubject(principal);

        var req = await _approvalRepository.GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null)
        {
            throw new ConsentRequestException("NOT_FOUND", $"Consent request '{requestId}' was not found.");
        }

        var isCrossTenantAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        var tenantId = ResolveTenantId(principal, req.TenantId, isCrossTenantAdmin);
        if (req.TenantId != tenantId && !isCrossTenantAdmin)
        {
            throw new ConsentRequestException("FORBIDDEN", "Cross-tenant access forbidden: The consent request belongs to another tenant.");
        }

        var userSid = principal.GetUserSid() ?? throw new ConsentRequestException("FORBIDDEN", "User SID could not be resolved from token.");
        var principalIdentifiers = ExtractPrincipalIdentifiers(principal, userSid.Value);
        bool isOwner = req.RequesterSid == userSid
            || (req.RequesterIdentifiers != null && req.RequesterIdentifiers.Any(id => principalIdentifiers.Contains(id, StringComparer.OrdinalIgnoreCase)));

        if (!isOwner)
        {
            throw new ConsentRequestException("FORBIDDEN", "Only the requester can withdraw their access request.");
        }

        if (req.Status != "PENDING" && req.Status != "PENDING_SECOND_APPROVAL" && req.Status != "PENDING_EXTERNAL_APPROVAL")
        {
            throw new ConsentRequestException("INVALID_STATE", "The consent request is no longer pending.");
        }

        var success = await _approvalRepository.WithdrawConsentRequestAsync(requestId, req.RequesterSid, ct).ConfigureAwait(false);
        if (!success)
        {
            throw new ConsentRequestException("INVALID_STATE", "The consent request could not be withdrawn.");
        }

        if (_eventOutbox != null)
        {
            req.Status = "WITHDRAWN";
            var now = _timeProvider.GetUtcNow();
            var payload = ConsentEventPayloads.ForRequest(req, now);
            await _eventOutbox.AppendAsync(
                new ConsentEventWrite(
                    req.TenantId.Value,
                    Guid.NewGuid(),
                    ConsentEventTypes.RequestUpdated,
                    req.Id.ToString("D"),
                    ConsentEventPayloads.Serialize(payload),
                    now,
                    req.ExternalSystemId),
                transaction: null,
                ct: ct).ConfigureAwait(false);
        }

        return true;
    }

    private void EnsurePersonSubject(ClaimsPrincipal? principal)
    {
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new ConsentRequestException("FORBIDDEN", "Authenticated principal is required.");
        }

        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentRequestException("FORBIDDEN", "Machine tokens (idtyp=app) cannot execute self-service access requests.");
        }

        if (principal.FindFirst(ClaimTypes.AuthenticationMethod)?.Value is "x509" or "Certificate"
            || principal.FindFirst("auth_method")?.Value is "mTLS" or "cert" or "certificate")
        {
            throw new ConsentRequestException("FORBIDDEN", "Certificate subjects cannot execute self-service access requests.");
        }

        if (principal.HasClaim(c => c.Type == "roles" && c.Value == "AppRole")
            && !principal.HasClaim(c => c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope"))
        {
            throw new ConsentRequestException("FORBIDDEN", "Application tokens cannot execute self-service access requests.");
        }

        if (_subjectResolver != null)
        {
            var subject = _subjectResolver.ResolveSubject(principal);
            if (subject.Provider == IdentityProviderType.ClientCertificate || subject.Type != SubjectType.User)
            {
                throw new ConsentRequestException("FORBIDDEN", "Self-service access requests require a person subject.");
            }
        }
    }
}
