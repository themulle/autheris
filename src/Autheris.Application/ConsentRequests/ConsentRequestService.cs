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
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IConsentRequestService"/>: validation, idempotency, tenant resolution, auto-approve and the
/// external approval (ITSM) hand-off, extracted from the GraphQL mutation. Failures surface as <see cref="ConsentRequestException"/>.
/// </summary>
public sealed class ConsentRequestService : IConsentRequestService
{
    private const string Operation = "RequestTableAccess";
    private const int MinJustificationLength = 5;
    private const int MaxJustificationLength = 2000;
    private const int MinDurationDays = 1;
    private const int MaxDurationDays = 365;
    private const int MaxIdempotencyKeyLength = 256;

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentApprovalRepository _approvalRepository;
    private readonly IIdempotencyStore? _idempotencyStore;
    private readonly IConsentRequestDispatcher? _dispatcher;
    private readonly IOptions<GatewayOptions>? _gatewayOptions;
    private readonly IGrantPolicy _grantPolicy;

    public ConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentApprovalRepository approvalRepository,
        IIdempotencyStore? idempotencyStore = null,
        IConsentRequestDispatcher? dispatcher = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IGrantPolicy? grantPolicy = null)
    {
        _grantPolicy = grantPolicy ?? new GrantPolicy();
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
        _idempotencyStore = idempotencyStore;
        _dispatcher = dispatcher;
        _gatewayOptions = gatewayOptions;
    }

    public async Task<SubmitConsentRequestResult> SubmitAsync(ConsentRequestSubmission command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Domain) || string.IsNullOrWhiteSpace(command.Schema) || string.IsNullOrWhiteSpace(command.TableName))
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

        var durationDays = command.DurationDays;
        if (durationDays < MinDurationDays || durationDays > MaxDurationDays)
        {
            throw Invalid("The duration (durationDays) must be between 1 and 365 days.");
        }

        var principal = command.Principal;
        var userSid = command.UserSid;
        var isCrossTenantAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

        var requestFingerprint = IdempotencyFingerprint(
            ResolveTenantId(principal, command.ContextTenant, isCrossTenantAdmin).Value,
            command.Domain, command.Schema, command.TableName, justification.Trim(), durationDays);
        var existing = await TryGetIdempotentAsync(userSid, command.IdempotencyKey, requestFingerprint, ct).ConfigureAwait(false);
        if (existing != null)
        {
            return existing;
        }

        var tableId = new TableIdentifier(command.Domain, command.Schema, command.TableName);
        var meta = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (meta == null)
        {
            // SEC (low): no table enumeration via NOT_FOUND + table name; neutral FORBIDDEN response.
            throw new ConsentRequestException("FORBIDDEN", "Access request for the specified table is not possible.");
        }

        // R-GQL-9: same ClusterAdmin exception as approve, reject and revoke.
        var tenantId = ResolveTenantId(principal, command.ContextTenant, isCrossTenantAdmin);

        // WP-SC-16: a user request is an individual grant; its duration is bounded by the class of the table (fail closed).
        var now = DateTimeOffset.UtcNow;
        var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
            GranteeType.User,
            userSid.Value,
            GrantKind.Consent,
            now.AddDays(durationDays),
            now,
            GrantClassifier.Classify([meta]),
            Requester: userSid.Value,
            Justification: justification));
        if (!grantDecision.Allowed)
        {
            throw new ConsentRequestException(grantDecision.ErrorCode ?? GrantErrorCodes.Indeterminate, grantDecision.Reason);
        }

        bool isItsmEnabled = _dispatcher != null && (_gatewayOptions?.Value.Itsm.Enabled == true);

        var request = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = userSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = userSid.Value,
            BusinessJustification = justification.Trim(),
            RequestedValidTo = now.AddDays(durationDays),
            Status = isItsmEnabled ? "PENDING_EXTERNAL_APPROVAL" : "PENDING",
            TenantId = tenantId,
            RequesterIdentifiers = ExtractPrincipalIdentifiers(principal, userSid.Value)
        };

        var created = await _approvalRepository.CreateConsentRequestAsync(request, ct).ConfigureAwait(false);

        if (_gatewayOptions?.Value.IsAutoApproveEnabled == true)
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

        if (isItsmEnabled)
        {
            ItsmTicketResult? ticketResult;
            try
            {
                var itsmRequest = new ItsmTicketRequest(
                    tenantId,
                    userSid,
                    tableId,
                    justification,
                    Math.Clamp(durationDays, MinDurationDays, MaxDurationDays),
                    null,
                    null);

                var preferredSystem = _gatewayOptions?.Value.Itsm.DefaultSystem ?? ItsmSystemType.ServiceNow;
                ticketResult = await _dispatcher!.DispatchAsync(itsmRequest, preferredSystem, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _approvalRepository.DeleteConsentRequestAsync(created.Id, ct).ConfigureAwait(false);
                throw new ConsentRequestException("ITSM_UNAVAILABLE", $"ITSM system unavailable: {ex.Message}");
            }

            if (ticketResult == null || !ticketResult.Success)
            {
                await _approvalRepository.DeleteConsentRequestAsync(created.Id, ct).ConfigureAwait(false);
                throw new ConsentRequestException(
                    ticketResult?.ErrorCode ?? "ITSM_UNAVAILABLE",
                    ticketResult?.ErrorMessage ?? "Failed to create external ITSM ticket.");
            }

            try
            {
                await _approvalRepository.UpdateConsentRequestTicketIdAsync(created.Id, ticketResult.TicketReference!.TicketId, ct).ConfigureAwait(false);
            }
            catch
            {
                await _approvalRepository.DeleteConsentRequestAsync(created.Id, ct).ConfigureAwait(false);
                throw new ConsentRequestException("ITSM_UNAVAILABLE", "Failed to record ITSM ticket reference.");
            }

            var pendingExternal = new SubmitConsentRequestResult
            {
                RequestId = created.Id,
                Status = "PENDING_EXTERNAL_APPROVAL",
                Message = "Consent request submitted to ITSM for external approval.",
                ItsmTicketReference = ticketResult.TicketReference
            };
            await StoreIdempotentAsync(userSid, command.IdempotencyKey, requestFingerprint, pendingExternal, ct).ConfigureAwait(false);
            return pendingExternal;
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
        var principalTenant = principal?.GetTenantId() ?? TenantId.LegacySingleTenant;

        if (principalTenant != TenantId.LegacySingleTenant &&
            contextTenant != TenantId.LegacySingleTenant &&
            principalTenant != contextTenant &&
            !isCrossTenantAdmin)
        {
            throw new ConsentRequestException("FORBIDDEN", "Cross-tenant access forbidden: The token tenant does not match the tenant of the connection context.");
        }

        return contextTenant != TenantId.LegacySingleTenant ? contextTenant : principalTenant;
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

    private static ConsentRequestException Invalid(string message) => new("INVALID_ARGUMENT", message);

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
}
