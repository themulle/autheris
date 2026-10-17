using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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
    /// WP-0 (SEC-113-R2-02): decision services are resolved from the request services with <c>GetRequiredService</c>; a missing registration
    /// (or missing request services) fails the call and never falls back to a permissive default.
    /// </summary>
    private static T RequireDecisionService<T>(IHttpContextAccessor? httpContextAccessor) where T : notnull
    {
        var services = httpContextAccessor?.HttpContext?.RequestServices
            ?? throw new InvalidOperationException("Request services are unavailable; the consent decision is refused (fail closed).");
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<T>(services);
    }

    /// <summary>
    /// WP-0 (P-4): the first step of every consent request decision. Only a person may approve or reject; read-only tokens, application
    /// principals (<c>idtyp=app</c>, app id without delegated scope), certificate subjects and unauthenticated callers are refused with
    /// <c>DECISION_NOT_PERMITTED</c> before any repository access. The denial is audited per principal and minute; an audit failure never
    /// changes the answer (the recorder does not throw and is optional by design: only the guard is a hard dependency).
    /// </summary>
    private static async Task EnsureDecisionPermittedAsync(IHttpContextAccessor? httpContextAccessor, CancellationToken ct)
    {
        var guard = RequireDecisionService<IConsentDecisionGuard>(httpContextAccessor);
        var principal = httpContextAccessor!.HttpContext!.User;
        var verdict = guard.Evaluate(principal);
        if (verdict.Allowed)
        {
            return;
        }

        var recorder = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<IConsentDecisionDenialRecorder>(
            httpContextAccessor.HttpContext.RequestServices);
        if (recorder != null)
        {
            await recorder.RecordAsync(principal, verdict.ReasonClass, GetContextTenantId(httpContextAccessor), ct).ConfigureAwait(false);
        }

        throw new GraphQLException(ErrorBuilder.New()
            .SetCode("DECISION_NOT_PERMITTED")
            .SetMessage("Only a person can approve or reject consent requests.")
            .Build());
    }

    /// <summary>
    /// WP-0 residual until the activation is atomic with the final step: the request is <c>APPROVED</c> without a consent. It is recoverable by the
    /// existing <c>ActivateConsentAsync</c> (status 'APPROVED' path). Logged at Error with ids and the exception type only, never request content.
    /// </summary>
    private static void LogConsentCreationFailed(IHttpContextAccessor? httpContextAccessor, ConsentRequest approved, Exception ex)
    {
        var services = httpContextAccessor?.HttpContext?.RequestServices;
        var logger = services != null
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<ILogger<Mutation>>(services)
            : null;
        logger?.LogError(
            "Consent creation after the final approval step failed for consent request {RequestId} (tenant {TenantId}, {ExceptionType}); the request stays APPROVED and is recoverable by activation.",
            approved.Id, approved.TenantId.Value, ex.GetType().Name);
    }

    private static GraphQLException NotPending() => new(ErrorBuilder.New()
        .SetCode(ConsentRequestStatusConflictException.ErrorCode)
        .SetMessage("The consent request is no longer pending.")
        .Build());

    /// <summary>
    /// R-GQL-9: the tenant of a mutation is the one resolved for the request (SecurityPrincipalContext, which honours a
    /// ClusterAdmin's X-Tenant-ID). The token claim only serves as a cross-check: a different claim tenant is forbidden,
    /// except for canonical ClusterAdmins. Without a context tenant the claim tenant applies.
    /// </summary>
    internal static TenantId ResolveMutationTenantId(IHttpContextAccessor? httpContextAccessor, ClaimsPrincipal? principal, bool isCrossTenantAdmin)
    {
        try
        {
            return ConsentRequestService.ResolveTenantId(principal, GetContextTenantId(httpContextAccessor), isCrossTenantAdmin);
        }
        catch (ConsentRequestException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetCode(ex.Code).SetMessage(ex.Message).Build());
        }
    }

    private static TenantId GetContextTenantId(IHttpContextAccessor? httpContextAccessor)
    {
        var contextTenant = TenantId.LegacySingleTenant;
        if (httpContextAccessor?.HttpContext?.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) == true && secObj is SecurityPrincipalContext secCtx)
        {
            contextTenant = secCtx.TenantId;
        }
        else if (httpContextAccessor?.HttpContext?.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            contextTenant = tid;
        }

        return contextTenant;
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

    public async Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        [Service] IConsentRequestService consentRequestService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
    {
        var userSid = GetAuthenticatedUserSid(httpContextAccessor);
        var principal = httpContextAccessor?.HttpContext?.User;

        try
        {
            var result = await consentRequestService.SubmitAsync(
                new ConsentRequestSubmission(
                    domain, schema, tableName, justification, durationDays, idempotencyKey,
                    userSid, principal, GetContextTenantId(httpContextAccessor)),
                ct);

            return new ConsentRequestPayload
            {
                RequestId = result.RequestId,
                Status = result.Status,
                Message = result.Message,
                ItsmTicketReference = result.ItsmTicketReference
            };
        }
        catch (ConsentRequestException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetCode(ex.Code).SetMessage(ex.Message).Build());
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
        await EnsureDecisionPermittedAsync(httpContextAccessor, ct).ConfigureAwait(false);
        var policy = RequireDecisionService<IGrantPolicy>(httpContextAccessor);

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

        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
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

        // WP-0 (P-3 part 1): the individual grant policy is evaluated on the loaded pending request BEFORE the step, on every approve call
        // (first and second step), so a denial writes nothing and leaves the request pending and rejectable. Unknown metadata classifies as
        // Sensitive (fail closed). Role and group grantees are standing grants and skip the policy.
        if (req.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal)
        {
            TableMetadata? grantMeta = metadataRepository != null
                ? await metadataRepository.GetTableMetadataAsync(req.TableIdentifier, ct)
                : null;
            var grantDecision = policy.Evaluate(new GrantRequest(
                req.RequestedGranteeType,
                req.RequestedGranteeRef,
                GrantKind.Consent,
                req.RequestedValidTo,
                DateTimeOffset.UtcNow,
                GrantClassifier.Classify([grantMeta]),
                Requester: req.RequesterSid.Value,
                Justification: req.BusinessJustification));
            if (!grantDecision.Allowed)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode(grantDecision.ErrorCode ?? "INDIVIDUAL_GRANT_INDETERMINATE")
                    .SetMessage(grantDecision.Reason)
                    .Build());
            }
        }

        ConsentRequest approved;
        try
        {
            approved = await approvalRepository.ApproveConsentRequestStepAsync(requestId, approverSid, ct);
        }
        catch (ConsentRequestStatusConflictException)
        {
            throw NotPending();
        }

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
            try
            {
                await consentRepository.CreateConsentAsync(consent, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Residual until the activation is atomic with the final step: the request is APPROVED without a consent and stays recoverable.
                LogConsentCreationFailed(httpContextAccessor, approved, ex);
                throw;
            }
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
        await EnsureDecisionPermittedAsync(httpContextAccessor, ct).ConfigureAwait(false);
        _ = RequireDecisionService<IGrantPolicy>(httpContextAccessor); // composition fail-fast: both decision resolvers need the same services

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

        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
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

        ConsentRequest rejected;
        try
        {
            rejected = await approvalRepository.RejectConsentRequestAsync(requestId, approverSid, reason.Trim(), ct);
        }
        catch (ConsentRequestStatusConflictException)
        {
            throw NotPending();
        }

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
        [Service] Autheris.Application.Interfaces.IAuditLogRepository auditLog = default!,
        [Service] Autheris.Application.Audit.IAuditWriter? auditWriter = null,
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

        var roles = principal.GetUserRoles();
        if (!roles.Contains("GovernanceAdmin") && !Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may reload the schema.")
                .Build());
        }

        await eventBus.PublishAsync("schema:reload", DateTimeOffset.UtcNow.ToString("O"), ct);

        // Review E-10: schema reloads change what the gateway exposes and belong in the audit chain.
        TenantId reloadTenant;
        try
        {
            reloadTenant = principal.GetTenantId();
        }
        catch (System.Security.SecurityException)
        {
            reloadTenant = TenantId.LegacySingleTenant; // cluster admins may have no tenant claim
        }

        var entry = new AuditLogEntry
        {
            TenantId = reloadTenant,
            EventType = "SCHEMA_RELOAD",
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = "{}"
        };

        if (auditWriter != null)
        {
            await auditWriter.RecordRequiredAsync(entry, ct);
        }
        else
        {
            await auditLog.RecordAuditEventAsync(entry, ct);
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

        // SR-L-23: only the canonical ClusterAdmin (claims-based, never ForwardAuth) or a GovernanceAdmin may synchronise.
        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!isClusterAdmin && !roles.Contains("GovernanceAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may synchronize OpenMetadata.")
                .Build());
        }

        // Non-cluster administrators are confined to their own tenant (fail-closed on an invalid tenant claim).
        TenantId? restrictToTenant = null;
        if (!isClusterAdmin)
        {
            try
            {
                restrictToTenant = principal.GetTenantId();
            }
            catch (System.Security.SecurityException)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("UNAUTHORIZED")
                    .SetMessage("No valid tenant in the authentication token.")
                    .Build());
            }
        }

        OpenMetadataSyncResult result;
        try
        {
            result = await syncService.SyncPermissionsAsync(dryRun, restrictToTenant, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException)
        {
            // Full detail stays in the server log (the sync service logs it); the caller gets a fixed code only.
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("SYNC_FAILED")
                .SetMessage("OpenMetadata synchronization failed.")
                .Build());
        }

        return new OpenMetadataSyncPayload
        {
            Success = result.Success,
            SyncedTables = result.SyncedTables,
            SyncedConsents = result.SyncedConsents,
            SyncedMaskingRules = result.SyncedMaskingRules,
            Warnings = result.SanitizedWarnings.ToList()
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
        => ConsentRequestService.ExtractPrincipalIdentifiers(principal, fallbackSid);

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
