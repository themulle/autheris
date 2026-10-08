using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Types;

public sealed class OpenMetadataSyncPayload
{
    public bool Success { get; init; }
    public int SyncedTables { get; init; }
    public int SyncedConsents { get; init; }
    public int SyncedMaskingRules { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class DataCatalogSyncPayload
{
    public bool Success { get; init; }
    public int SyncedTablesCount { get; init; }
    public int SyncedColumnsCount { get; init; }
    public int MaskedColumnsCount { get; init; }
    public int Art9ProtectedTablesCount { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class ConsentRequestPayload
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ItsmTicketReference? ItsmTicketReference { get; init; }
}

public sealed class Mutation
{
    private static Sid GetAuthenticatedUserSid(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        var userSid = httpContext.User.GetUserSid();
        if (userSid == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("No valid user SID in the authentication token.")
                .Build());
        }

        return userSid.Value;
    }

    /// <summary>
    /// R-GQL-9: the tenant of a mutation is the one resolved for the request (SecurityPrincipalContext, which honours a
    /// ClusterAdmin's X-Tenant-ID). The token claim only serves as a cross-check: a different claim tenant is forbidden,
    /// except for canonical ClusterAdmins. Without a context tenant the claim tenant applies.
    /// </summary>
    internal static TenantId ResolveMutationTenantId(IHttpContextAccessor? httpContextAccessor, ClaimsPrincipal? principal, bool isCrossTenantAdmin)
    {
        var principalTenant = principal?.GetTenantId() ?? TenantId.LegacySingleTenant;
        var contextTenant = TenantId.LegacySingleTenant;
        if (httpContextAccessor?.HttpContext?.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) == true && secObj is SecurityPrincipalContext secCtx)
        {
            contextTenant = secCtx.TenantId;
        }
        else if (httpContextAccessor?.HttpContext?.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            contextTenant = tid;
        }

        if (principalTenant != TenantId.LegacySingleTenant &&
            contextTenant != TenantId.LegacySingleTenant &&
            principalTenant != contextTenant &&
            !isCrossTenantAdmin)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Cross-tenant access forbidden: The token tenant does not match the tenant of the connection context.")
                .Build());
        }

        return contextTenant != TenantId.LegacySingleTenant ? contextTenant : principalTenant;
    }

    /// <summary>
    /// GQL-11: an idempotency key only replays the result of the same operation with the same arguments in the same
    /// tenant; reusing a key for other arguments must not return the cached result of a different request.
    /// </summary>
    internal static string IdempotencyFingerprint(params object?[] parts)
    {
        var material = string.Join('\u001f', parts.Select(p => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))[..32];
    }

    private const int MaxIdempotencyKeyLength = 256;

    private static string? ValidateAndNormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        var trimmed = idempotencyKey.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException($"Idempotency key exceeds maximum allowed length of {MaxIdempotencyKeyLength} characters.", nameof(idempotencyKey));
        }
        return Uri.EscapeDataString(trimmed);
    }

    private static async Task<ConsentRequestPayload?> TryGetIdempotentAsync(
        Sid userSid,
        string operation,
        string? idempotencyKey,
        string argumentsFingerprint,
        IIdempotencyStore? idempotencyStore,
        CancellationToken ct = default)
    {
        var normalizedKey = ValidateAndNormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey == null || idempotencyStore == null) return null;
        var compositeKey = $"idempotency:{userSid.Value}:{operation}:{normalizedKey}:{argumentsFingerprint}";
        return await idempotencyStore.GetAsync<ConsentRequestPayload>(compositeKey, ct).ConfigureAwait(false);
    }

    private static async Task StoreIdempotentAsync(
        Sid userSid,
        string operation,
        string? idempotencyKey,
        string argumentsFingerprint,
        ConsentRequestPayload payload,
        IIdempotencyStore? idempotencyStore,
        CancellationToken ct = default)
    {
        var normalizedKey = ValidateAndNormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey == null || idempotencyStore == null) return;
        var compositeKey = $"idempotency:{userSid.Value}:{operation}:{normalizedKey}:{argumentsFingerprint}";
        await idempotencyStore.SetIfNotExistsAsync(compositeKey, payload, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    [GraphQLIgnore]
    public Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] IIdempotencyStore idempotencyStore = default!,
        CancellationToken ct = default)
        => RequestTableAccessAsync(domain, schema, tableName, justification, durationDays, idempotencyKey, repository, repository, httpContextAccessor, idempotencyStore, null, null, ct);

    public async Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        [Service] ITableMetadataRepository metadataRepository = default!,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IIdempotencyStore idempotencyStore = default!,
        [Service] ItsmWorkflowDispatcher? itsmDispatcher = default!,
        [Service] IOptions<GatewayOptions>? gatewayOptions = default!,
        CancellationToken ct = default)
    {
        var userSid = GetAuthenticatedUserSid(httpContextAccessor);
        var principal = httpContextAccessor?.HttpContext?.User;

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(tableName))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("Domain, Schema and TableName must be valid, non-empty values.")
                .Build());
        }

        if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < 5)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("A justification of at least 5 characters is required.")
                .Build());
        }

        if (justification.Length > 2000)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("The justification must not exceed 2000 characters.")
                .Build());
        }

        if (durationDays < 1 || durationDays > 365)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("The duration (durationDays) must be between 1 and 365 days.")
                .Build());
        }

        var requestFingerprint = IdempotencyFingerprint(
            ResolveMutationTenantId(httpContextAccessor, principal, Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)).Value,
            domain, schema, tableName, justification.Trim(), durationDays);
        var existing = await TryGetIdempotentAsync(userSid, "RequestTableAccess", idempotencyKey, requestFingerprint, idempotencyStore, ct);
        if (existing != null)
        {
            return existing;
        }

        var tableId = new TableIdentifier(domain, schema, tableName);
        var meta = await metadataRepository.GetTableMetadataAsync(tableId, ct);
        if (meta == null)
        {
            // SEC (Niedrig): Keine Tabellen-Enumeration über NOT_FOUND + Tabellenname; neutrale FORBIDDEN-Antwort.
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Access request for the specified table is not possible.")
                .Build());
        }

        // R-GQL-9: same ClusterAdmin exception as approve, reject and revoke.
        bool isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin);

        bool isItsmEnabled = itsmDispatcher != null && (gatewayOptions?.Value.Itsm.Enabled == true);

        var request = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = userSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = userSid.Value,
            BusinessJustification = justification.Trim(),
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(durationDays),
            Status = isItsmEnabled ? "PENDING_EXTERNAL_APPROVAL" : "PENDING",
            TenantId = tenantId,
            RequesterIdentifiers = ExtractPrincipalIdentifiers(principal, userSid.Value)
        };

        var created = await approvalRepository.CreateConsentRequestAsync(request, ct);

        if (gatewayOptions?.Value.IsAutoApproveEnabled == true)
        {
            await approvalRepository.ActivateConsentForAutoApproveAsync(created.Id, ct);
            var payload = new ConsentRequestPayload
            {
                RequestId = created.Id,
                Status = "APPROVED",
                Message = "[INSECURE GETTING STARTED] Consent request auto-approved."
            };
            await StoreIdempotentAsync(userSid, "RequestTableAccess", idempotencyKey, requestFingerprint, payload, idempotencyStore, ct);
            return payload;
        }

        if (isItsmEnabled)
        {
            ItsmTicketResult? ticketResult = null;
            try
            {
                var itsmRequest = new ItsmTicketRequest(
                    tenantId,
                    userSid,
                    tableId,
                    justification,
                    Math.Clamp(durationDays, 1, 365),
                    null,
                    null);

                var preferredSystem = gatewayOptions?.Value.Itsm.DefaultSystem ?? ItsmSystemType.ServiceNow;
                ticketResult = await itsmDispatcher!.DispatchTicketRequestAsync(itsmRequest, preferredSystem, ct);
            }
            catch (Exception ex)
            {
                await approvalRepository.DeleteConsentRequestAsync(created.Id, ct);
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("ITSM_UNAVAILABLE")
                    .SetMessage($"ITSM system unavailable: {ex.Message}")
                    .Build());
            }

            if (ticketResult == null || !ticketResult.Success)
            {
                await approvalRepository.DeleteConsentRequestAsync(created.Id, ct);
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode(ticketResult?.ErrorCode ?? "ITSM_UNAVAILABLE")
                    .SetMessage(ticketResult?.ErrorMessage ?? "Failed to create external ITSM ticket.")
                    .Build());
            }

            try
            {
                await approvalRepository.UpdateConsentRequestTicketIdAsync(created.Id, ticketResult.TicketReference!.TicketId, ct);
            }
            catch
            {
                await approvalRepository.DeleteConsentRequestAsync(created.Id, ct);
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("ITSM_UNAVAILABLE")
                    .SetMessage("Failed to record ITSM ticket reference.")
                    .Build());
            }

            var payload = new ConsentRequestPayload
            {
                RequestId = created.Id,
                Status = "PENDING_EXTERNAL_APPROVAL",
                Message = "Consent request submitted to ITSM for external approval.",
                ItsmTicketReference = ticketResult.TicketReference
            };

            await StoreIdempotentAsync(userSid, "RequestTableAccess", idempotencyKey, requestFingerprint, payload, idempotencyStore, ct);
            return payload;
        }
        else
        {
            var payload = new ConsentRequestPayload
            {
                RequestId = created.Id,
                Status = created.Status,
                Message = "Consent request submitted successfully."
            };

            await StoreIdempotentAsync(userSid, "RequestTableAccess", idempotencyKey, requestFingerprint, payload, idempotencyStore, ct);
            return payload;
        }
    }

    [GraphQLIgnore]
    public Task<ConsentRequestPayload> ApproveConsentRequestAsync(
        Guid requestId,
        string? idempotencyKey,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] IIdempotencyStore idempotencyStore = default!,
        CancellationToken ct = default)
        => ApproveConsentRequestAsync(requestId, idempotencyKey, repository, repository, repository, repository, httpContextAccessor, idempotencyStore, ct);

    [GraphQLIgnore]
    public Task<ConsentRequestPayload> ApproveConsentRequestAsync(
        Guid requestId,
        string? idempotencyKey,
        [Service] IConsentApprovalRepository approvalRepository,
        [Service] IDataOwnershipRepository ownershipRepository,
        [Service] IConsentRepository consentRepository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => ApproveConsentRequestAsync(
            requestId,
            idempotencyKey,
            approvalRepository,
            ownershipRepository,
            consentRepository,
            (approvalRepository as ITableMetadataRepository) ?? (consentRepository as ITableMetadataRepository)!,
            httpContextAccessor,
            default!,
            ct);

    public async Task<ConsentRequestPayload> ApproveConsentRequestAsync(
        Guid requestId,
        string? idempotencyKey,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IConsentRepository consentRepository = default!,
        [Service] ITableMetadataRepository metadataRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IIdempotencyStore idempotencyStore = default!,
        CancellationToken ct = default)
    {
        var approverSid = GetAuthenticatedUserSid(httpContextAccessor);

        var approvePrincipal = httpContextAccessor?.HttpContext?.User;
        var approveFingerprint = IdempotencyFingerprint(
            ResolveMutationTenantId(httpContextAccessor, approvePrincipal, Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(approvePrincipal)).Value,
            requestId);
        var existing = await TryGetIdempotentAsync(approverSid, "ApproveConsentRequest", idempotencyKey, approveFingerprint, idempotencyStore, ct);
        if (existing != null)
        {
            return existing;
        }

        var principal = httpContextAccessor?.HttpContext?.User;

        // Verify request existence and approver authorization (F-AUTH-05)
        var req = await approvalRepository.GetConsentRequestAsync(requestId, ct);
        if (req == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent request '{requestId}' was not found.")
                .Build());
        }

        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        bool isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal); // RR-L2-01: single cross-tenant definition
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || isCrossTenantAdmin;

        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin);

        if (req.TenantId != tenantId && !isCrossTenantAdmin)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Cross-tenant access forbidden: The consent request belongs to another tenant.")
                .Build());
        }

        // Four-Eyes Principle / Separation of Duties (Funktionstrennung)
        // SEC M-3: Multi-IdP / Multi-claim self-approval detection (prevents OID vs Kerberos SID self-approval in both directions)
        if (req.RequesterSid == approverSid || IsSameIdentity(req, principal))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Separation of duties violated: The requester cannot approve their own consent request.")
                .Build());
        }

        if (!isPrivilegedAdmin)
        {
            var isAuthorized = await ownershipRepository.IsAuthorizedApproverForTableAsync(req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage($"User '{approverSid}' is neither a data owner nor a delegated approver for '{req.TableIdentifier}' and has no approver role.")
                    .Build());
            }
        }

        var approved = await approvalRepository.ApproveConsentRequestStepAsync(requestId, approverSid, ct);

        // Automatically create active consent ONLY upon final full approval (Fix 2.4: honor RequestedGranteeType)
        if (string.Equals(approved.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            var isRole = approved.RequestedGranteeType == GranteeType.Role;
            var columnRules = new List<ConsentColumnRule>();

            if (metadataRepository != null)
            {
                var tableMeta = await metadataRepository.GetTableMetadataAsync(approved.TableIdentifier, ct);
                if (tableMeta != null && tableMeta.Columns.Count > 0)
                {
                    // POL-3 / R-POL-11: same snapshot rule as the ITSM activation in the repositories.
                    columnRules.AddRange(ConsentColumnSnapshot.FromMetadata(tableMeta));
                }
            }

            var consent = new Consent
            {
                TableId = approved.TableId,
                TableIdentifier = approved.TableIdentifier,
                ConsentRequestId = approved.Id,
                TenantId = approved.TenantId,
                Effect = ConsentEffect.Allow,
                GranteeType = approved.RequestedGranteeType,
                GranteeSid = isRole ? (Sid?)null : new Sid(approved.RequestedGranteeRef),
                RoleName = isRole ? approved.RequestedGranteeRef : null,
                ValidFrom = DateTimeOffset.UtcNow,
                ValidTo = approved.RequestedValidTo,
                CreatedBySid = approverSid,
                ColumnRules = columnRules
            };
            await consentRepository.CreateConsentAsync(consent, ct);
        }

        var message = string.Equals(approved.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)
            ? "Consent request approved and active consent created."
            : $"Consent request approved step completed. Current status: {approved.Status}.";

        var payload = new ConsentRequestPayload
        {
            RequestId = approved.Id,
            Status = approved.Status,
            Message = message
        };

        await StoreIdempotentAsync(approverSid, "ApproveConsentRequest", idempotencyKey, approveFingerprint, payload, idempotencyStore, ct);
        return payload;
    }

    [GraphQLIgnore]
    public Task<ConsentRequestPayload> RejectConsentRequestAsync(
        Guid requestId,
        string reason,
        string? idempotencyKey,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] IIdempotencyStore idempotencyStore = default!,
        CancellationToken ct = default)
        => RejectConsentRequestAsync(requestId, reason, idempotencyKey, repository, repository, httpContextAccessor, idempotencyStore, ct);

    public async Task<ConsentRequestPayload> RejectConsentRequestAsync(
        Guid requestId,
        string reason,
        string? idempotencyKey,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IIdempotencyStore idempotencyStore = default!,
        CancellationToken ct = default)
    {
        var approverSid = GetAuthenticatedUserSid(httpContextAccessor);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("A rejection reason of at least 3 characters is required.")
                .Build());
        }

        if (reason.Length > 1000)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("The rejection reason must not exceed 1000 characters.")
                .Build());
        }

        var rejectPrincipal = httpContextAccessor?.HttpContext?.User;
        var rejectFingerprint = IdempotencyFingerprint(
            ResolveMutationTenantId(httpContextAccessor, rejectPrincipal, Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(rejectPrincipal)).Value,
            requestId, reason.Trim());
        var existing = await TryGetIdempotentAsync(approverSid, "RejectConsentRequest", idempotencyKey, rejectFingerprint, idempotencyStore, ct);
        if (existing != null)
        {
            return existing;
        }

        var principal = httpContextAccessor?.HttpContext?.User;

        var req = await approvalRepository.GetConsentRequestAsync(requestId, ct);
        if (req == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent request '{requestId}' was not found.")
                .Build());
        }

        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        bool isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal); // RR-L2-01: single cross-tenant definition
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || isCrossTenantAdmin;

        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin);

        if (req.TenantId != tenantId && !isCrossTenantAdmin)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Cross-tenant access forbidden: The consent request belongs to another tenant.")
                .Build());
        }

        if (!isPrivilegedAdmin)
        {
            var isAuthorized = await ownershipRepository.IsAuthorizedApproverForTableAsync(req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage($"User '{approverSid}' is neither a data owner nor a delegated approver for '{req.TableIdentifier}' and has no approver role.")
                    .Build());
            }
        }

        var rejected = await approvalRepository.RejectConsentRequestAsync(requestId, approverSid, reason.Trim(), ct);
        var payload = new ConsentRequestPayload
        {
            RequestId = rejected.Id,
            Status = rejected.Status,
            Message = $"Consent request rejected: {reason.Trim()}"
        };

        await StoreIdempotentAsync(approverSid, "RejectConsentRequest", idempotencyKey, rejectFingerprint, payload, idempotencyStore, ct);
        return payload;
    }

    [GraphQLIgnore]
    public Task<bool> RevokeConsentAsync(
        Guid consentId,
        string reason,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => RevokeConsentAsync(consentId, reason, repository, repository, httpContextAccessor, ct);

    public async Task<bool> RevokeConsentAsync(
        Guid consentId,
        string reason,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var revokerSid = GetAuthenticatedUserSid(httpContextAccessor);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("A revocation reason of at least 3 characters is required.")
                .Build());
        }

        if (reason.Length > 1000)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_ARGUMENT")
                .SetMessage("The revocation reason must not exceed 1000 characters.")
                .Build());
        }

        var consent = await consentRepository.GetConsentByIdAsync(consentId, ct);
        if (consent == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent '{consentId}' was not found.")
                .Build());
        }

        var principal = httpContextAccessor?.HttpContext?.User;
        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        bool isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal); // RR-L2-01: single cross-tenant definition
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || isCrossTenantAdmin;

        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin);

        if (consent.TenantId != tenantId && !isCrossTenantAdmin)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Cross-tenant access forbidden: The consent belongs to another tenant.")
                .Build());
        }

        bool isGranteeSelf = consent.GranteeType == GranteeType.User &&
                             consent.GranteeSid.HasValue &&
                             consent.GranteeSid.Value == revokerSid;

        bool isOwnerOrDelegate = false;
        if (!isPrivilegedAdmin && !isGranteeSelf)
        {
            isOwnerOrDelegate = await ownershipRepository.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, revokerSid, ct);
        }

        if (!isPrivilegedAdmin && !isGranteeSelf && !isOwnerOrDelegate)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage($"User '{revokerSid}' is neither a GovernanceAdmin, data owner or delegated approver for '{consent.TableIdentifier}', nor the beneficiary.")
                .Build());
        }

        await consentRepository.RevokeConsentAsync(consentId, revokerSid, reason, ct);
        return true;
    }

    public async Task<bool> ReloadSchemaAsync(
        [Service] IEventBus eventBus = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] Autheris.Application.Interfaces.IAuditLogRepository? auditLog = null,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!roles.Contains("GovernanceAdmin") && !roles.Contains("ClusterAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may reload the schema.")
                .Build());
        }

        await eventBus.PublishAsync("schema:reload", DateTimeOffset.UtcNow.ToString("O"), ct);

        // Review E-10: schema reloads change what the gateway exposes and belong in the audit chain.
        if (auditLog != null)
        {
            TenantId reloadTenant;
            try
            {
                reloadTenant = principal.GetTenantId();
            }
            catch (System.Security.SecurityException)
            {
                reloadTenant = TenantId.LegacySingleTenant; // cluster admins may have no tenant claim
            }

            await auditLog.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = reloadTenant,
                EventType = "SCHEMA_RELOAD",
                ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
                TargetTable = string.Empty,
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = "{}"
            }, ct);
        }

        return true;
    }

    public async Task<OpenMetadataSyncPayload> SyncOpenMetadataAsync(
        bool dryRun = false,
        [Service] IOpenMetadataSyncService syncService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!roles.Contains("GovernanceAdmin") && !roles.Contains("ClusterAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may synchronize OpenMetadata.")
                .Build());
        }

        var result = await syncService.SyncPermissionsAsync(dryRun, ct);
        return new OpenMetadataSyncPayload
        {
            Success = result.Success,
            SyncedTables = result.SyncedTables,
            SyncedConsents = result.SyncedConsents,
            SyncedMaskingRules = result.SyncedMaskingRules,
            Warnings = result.Warnings.ToList()
        };
    }

    public async Task<DataCatalogSyncPayload> SyncDataCatalogAsync(
        bool dryRun = false,
        [Service] IDataCatalogSyncService catalogSyncService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!roles.Contains("GovernanceAdmin") && !roles.Contains("ClusterAdmin") && !roles.Contains("DeveloperAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may synchronize the data catalog.")
                .Build());
        }

        var result = await catalogSyncService.SyncCatalogAsync(dryRun, ct);
        return new DataCatalogSyncPayload
        {
            Success = result.Success,
            SyncedTablesCount = result.SyncedTablesCount,
            SyncedColumnsCount = result.SyncedColumnsCount,
            MaskedColumnsCount = result.MaskedColumnsCount,
            Art9ProtectedTablesCount = result.Art9ProtectedTablesCount,
            Warnings = result.Warnings.ToList()
        };
    }

    private static List<string> ExtractPrincipalIdentifiers(ClaimsPrincipal? principal, string? fallbackSid = null)
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

    private static bool IsSameIdentity(ConsentRequest req, ClaimsPrincipal? principal)
    {
        if (principal == null)
        {
            return false;
        }

        var approverIds = ExtractPrincipalIdentifiers(principal);
        var requesterIds = req.RequesterIdentifiers.ToList();
        if (!string.IsNullOrWhiteSpace(req.RequesterSid.Value))
        {
            requesterIds.Add(req.RequesterSid.Value);
        }

        foreach (var reqId in requesterIds)
        {
            if (approverIds.Any(apprId => string.Equals(reqId, apprId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
