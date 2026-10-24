using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Validation;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public string? Description { get; init; }
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

    internal static void EnsureReviewScope(IHttpContextAccessor? httpContextAccessor, ClaimsPrincipal? principal)
    {
        var gatewayOptions = httpContextAccessor?.HttpContext?.RequestServices != null
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>(httpContextAccessor.HttpContext.RequestServices)?.Value
            : null;
        var entraId = gatewayOptions?.Authentication?.EntraId;
        if (entraId != null && (!entraId.SelfServiceEnabled || entraId.AccessReviewScopes is { Count: 0 }))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Self-service access review is disabled.")
                .Build());
        }

        var scopes = TokenAccessScope.GetScopeSet(principal?.Claims);
        if (scopes.Count == 0) return;

        var configuredReviewScopes = entraId?.AccessReviewScopes;
        var reviewScopes = configuredReviewScopes is { Count: > 0 } ? configuredReviewScopes : ["data.access.review"];
        if (!reviewScopes.Any(s => scopes.Contains(s)))
        {
            var formattedScopes = string.Join("', '", reviewScopes);
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage($"The '{formattedScopes}' scope is required to review consent requests.")
                .Build());
        }
    }

    private static string? ValidateOptionalConsentDescription(IHttpContextAccessor? httpContextAccessor, string? description)
    {
        if (description is null) return null;

        var textOptions = httpContextAccessor?.HttpContext?.RequestServices != null
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<Microsoft.Extensions.Options.IOptions<GovernanceTextOptions>>(httpContextAccessor.HttpContext.RequestServices)
            : null;
        var defaultOptions = new GovernanceTextOptions();
        var maxLength = textOptions?.Value?.ConsentDescriptionMaxLength ?? defaultOptions.ConsentDescriptionMaxLength;
        var check = UntrustedText.Normalize(description, maxLength, allowLineFeed: false);
        if (!check.IsValid)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
                .SetMessage("The description is not valid.")
                .Build());
        }
        return check.Value;
    }

    private static GraphQLException ConsentNotFound() => new(ErrorBuilder.New()
        .SetCode("NOT_FOUND")
        .SetMessage("The consent was not found.")
        .Build());

    /// <summary>
    /// PLAN-GOV-158-INC1 decision 7: sets or clears the owner-authored description of a consent. Order (SEC-GOV158I1-04): decision guard (a person only),
    /// tenant-scoped load, table-scoped admin/owner/delegate check, then text validation, then the write. Every refusal before the text is judged (unknown,
    /// foreign tenant, revoked, expired, not permitted, ownership lookup failure) is the same fixed <c>NOT_FOUND</c>, so there is no validation or
    /// existence oracle. The description never influences a decision; it is stored with a Tier-A audit row (length and hash prefix only) in one transaction.
    /// </summary>
    public async Task<ConsentDto> SetConsentDescriptionAsync(
        Guid consentId,
        string? description,
        [Service] IConsentRepository consentRepository,
        [Service] IConsentDescriptionRepository descriptionRepository,
        [Service] IDataOwnershipRepository ownershipRepository,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] IOptions<GovernanceTextOptions>? textOptions = null,
        CancellationToken ct = default)
    {
        await EnsureDecisionPermittedAsync(httpContextAccessor, ct).ConfigureAwait(false);
        var actorSid = GetAuthenticatedUserSid(httpContextAccessor);
        var principal = httpContextAccessor!.HttpContext!.User;

        var isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin);

        var consent = await consentRepository.GetConsentByIdAsync(consentId, ct).ConfigureAwait(false);
        if (consent == null
            || (consent.TenantId != tenantId && !isCrossTenantAdmin)
            || !consent.IsActive(DateTimeOffset.UtcNow))
        {
            throw ConsentNotFound();
        }

        var isAdmin = isCrossTenantAdmin || principal.IsInRole("GovernanceAdmin");
        if (!isAdmin)
        {
            bool permitted;
            try
            {
                permitted = await ownershipRepository.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, actorSid, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                permitted = false; // fail closed: a failing ownership lookup denies
            }

            if (!permitted)
            {
                throw ConsentNotFound();
            }
        }

        var maxLength = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;
        var check = UntrustedText.Normalize(description, maxLength, allowLineFeed: false);
        if (!check.IsValid)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
                .SetMessage("The description is not valid.")
                .Build());
        }

        // The repository read does not hydrate the description (INC1 opt-in); load it so the audit row fingerprints the real old value.
        await Autheris.Application.Governance.ConsentDescriptionLoader.LoadAsync(consentRepository, [consent], ct).ConfigureAwait(false);
        var (oldLength, oldHash) = ConsentDescriptionText.Fingerprint(consent.Description);
        var (newLength, newHash) = ConsentDescriptionText.Fingerprint(check.Value);
        var audit = new AuditLogEntry
        {
            TenantId = consent.TenantId,
            EventType = AuditEventTypes.ConsentDescriptionChanged,
            ActorSid = actorSid,
            TargetTable = consent.TableIdentifier.ToString(),
            Decision = "UPDATED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ConsentId = consent.Id,
                Table = consent.TableIdentifier.ToString(),
                OldLength = oldLength,
                OldSha256Prefix = oldHash,
                NewLength = newLength,
                NewSha256Prefix = newHash
            })
        };

        if (!await descriptionRepository.UpdateConsentDescriptionWithAuditAsync(consent.Id, consent.TenantId, check.Value, audit, ct).ConfigureAwait(false))
        {
            throw ConsentNotFound();
        }

        return new ConsentDto
        {
            Id = consent.Id,
            TableIdentifier = consent.TableIdentifier.ToString(),
            Effect = consent.Effect.ToString(),
            GranteeType = consent.GranteeType.ToString(),
            GranteeSid = consent.GranteeSid?.Value,
            RoleName = consent.RoleName,
            ValidFrom = consent.ValidFrom,
            ValidTo = consent.ValidTo,
            Columns = consent.ColumnRules.Select(cr => cr.ColumnName).ToList(),
            Description = ConsentDescriptionText.ForOutput(check.Value, maxLength)
        };
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

    [GraphQLIgnore]
    public Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        [Service] IConsentRequestService consentRequestService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => RequestTableAccessAsync(domain, schema, tableName, justification, durationDays, idempotencyKey, null, consentRequestService, httpContextAccessor, ct);

    public async Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        AccessRequestContextInput? context = null,
        [Service] IConsentRequestService consentRequestService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var userSid = GetAuthenticatedUserSid(httpContextAccessor);
        var principal = httpContextAccessor?.HttpContext?.User;

        try
        {
            var result = await consentRequestService.SubmitAsync(
                new ConsentRequestSubmission(
                    domain, schema, tableName, justification, durationDays, idempotencyKey,
                    userSid, principal, GetContextTenantId(httpContextAccessor), context),
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

    public async Task<ConsentRequestPayload> WithdrawAccessRequestAsync(
        Guid requestId,
        [Service] IConsentRequestService consentRequestService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        try
        {
            await consentRequestService.WithdrawAsync(requestId, principal, ct).ConfigureAwait(false);
            return new ConsentRequestPayload
            {
                RequestId = requestId,
                Status = "WITHDRAWN",
                Message = "Consent request withdrawn."
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
        => ApproveConsentRequestAsync(requestId, idempotencyKey, repository, repository, repository, repository, httpContextAccessor, idempotencyStore, null, null, null, ct);

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
            null,
            null,
            null,
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
        RowFilterInput? rowFilter = null,
        ColumnMaskingInput? columnMasking = null,
        string? description = null,
        CancellationToken ct = default)
    {
        await EnsureDecisionPermittedAsync(httpContextAccessor, ct).ConfigureAwait(false);
        var policy = RequireDecisionService<IGrantPolicy>(httpContextAccessor);

        var approverSid = GetAuthenticatedUserSid(httpContextAccessor);

        var approvePrincipal = httpContextAccessor.HttpContext?.User;
        EnsureReviewScope(httpContextAccessor, approvePrincipal);

        string? validatedDescription = ValidateOptionalConsentDescription(httpContextAccessor, description);

        var approveFingerprint = IdempotencyFingerprint(
            ResolveMutationTenantId(httpContextAccessor, approvePrincipal, Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(approvePrincipal)).Value,
            requestId,
            validatedDescription ?? string.Empty,
            // GQL-06: a retry with the same key but different restrictions must not replay the earlier result.
            rowFilter == null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(rowFilter),
            columnMasking == null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(columnMasking));
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

        TableMetadata? tableMeta = metadataRepository != null
            ? await metadataRepository.GetTableMetadataAsync(req.TableIdentifier, ct)
            : null;

        IReadOnlyList<ConsentRowFilter> builtRowFilters = Array.Empty<ConsentRowFilter>();
        if (rowFilter != null && rowFilter.Predicates != null && rowFilter.Predicates.Count > 0)
        {
            if (tableMeta == null)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("BAD_REQUEST")
                    .SetMessage($"Metadata for table '{req.TableIdentifier}' could not be resolved for row filter validation.")
                    .Build());
            }
            try
            {
                builtRowFilters = RowFilterAstValidator.BuildRowFilters(rowFilter, tableMeta, Guid.NewGuid());
            }
            catch (ConsentRequestException ex)
            {
                throw new GraphQLException(ErrorBuilder.New().SetCode(ex.Code).SetMessage(ex.Message).Build());
            }
        }

        if (columnMasking != null && columnMasking.Rules != null && columnMasking.Rules.Count > 0)
        {
            if (tableMeta == null)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("BAD_REQUEST")
                    .SetMessage($"Metadata for table '{req.TableIdentifier}' could not be resolved for column masking validation.")
                    .Build());
            }
            var validCols = tableMeta.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in columnMasking.Rules)
            {
                if (!validCols.ContainsKey(rule.ColumnName))
                {
                    throw new GraphQLException(ErrorBuilder.New()
                        .SetCode("BAD_REQUEST")
                        .SetMessage($"Column '{rule.ColumnName}' does not exist on table '{req.TableIdentifier}'.")
                        .Build());
                }
            }
        }

        var columnRules = new List<ConsentColumnRule>();
        if (tableMeta != null && tableMeta.Columns.Count > 0)
        {
            var baseRules = ConsentColumnSnapshot.FromMetadata(tableMeta);
            if (columnMasking?.Rules != null && columnMasking.Rules.Count > 0)
            {
                var maskMap = columnMasking.Rules.ToDictionary(r => r.ColumnName, r => r, StringComparer.OrdinalIgnoreCase);
                columnRules.AddRange(baseRules.Select(r =>
                {
                    if (maskMap.TryGetValue(r.ColumnName, out var maskRule))
                    {
                        var lvl = string.Equals(maskRule.MaskingType, "None", StringComparison.OrdinalIgnoreCase)
                            ? ColumnAccessLevel.Clear
                            : ColumnAccessLevel.Mask;
                        return new ConsentColumnRule
                        {
                            Id = r.Id,
                            ConsentId = r.ConsentId,
                            TableColumnId = r.TableColumnId,
                            ColumnName = r.ColumnName,
                            AccessLevel = lvl,
                            ApplicableScopes = r.ApplicableScopes
                        };
                    }
                    return r;
                }));
            }
            else
            {
                columnRules.AddRange(baseRules);
            }
        }

        // ADR-008 / D-4: the canonical four-eyes rule (explicit flag OR CONFIDENTIAL/HIGH/RESTRICTED/SECRET sensitivity), identical to
        // ConsentApprovalPolicy.RequiresFourEyesApproval in the repositories, which stay authoritative under the row lock.
        bool requiresFourEyes = tableMeta?.Table.IsHighlySensitive ?? false;
        // Not every read model populates ApprovalSteps (SQLite does not): the status after the first step counts as well.
        bool firstStepRecorded =
            string.Equals(req.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) ||
            req.ApprovalSteps.Any(s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase));
        bool isFinalStep = !requiresFourEyes || firstStepRecorded;
        bool hasRestrictions = builtRowFilters.Count > 0 || (columnMasking?.Rules != null && columnMasking.Rules.Count > 0);

        // Security review: restrictions are only applied with the final approval that creates the consent. On the first step of a
        // four-eyes request they would be dropped and the second approver could approve unrestricted, so they are refused (fail closed).
        if (hasRestrictions && !isFinalStep)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("BAD_REQUEST")
                .SetMessage($"Consent request '{requestId}' requires four-eyes approval: rowFilter and columnMasking can only be supplied with the second (final) approval, not with the first step.")
                .Build());
        }

        ConsentRequest approved;
        if (hasRestrictions)
        {
            var isRole = req.RequestedGranteeType == GranteeType.Role;
            var step = new ApprovalStep
            {
                Id = Guid.NewGuid(),
                ConsentRequestId = requestId,
                StepNumber = req.ApprovalSteps.Count + 1, // informational: the repository assigns the step number under the row lock
                ApproverSid = approverSid,
                Decision = "APPROVED",
                DecidedAt = DateTimeOffset.UtcNow
            };
            var consent = new Consent
            {
                Id = Guid.NewGuid(),
                TableId = req.TableId,
                TableIdentifier = req.TableIdentifier,
                ConsentRequestId = req.Id,
                TenantId = req.TenantId,
                Effect = ConsentEffect.Allow,
                GranteeType = req.RequestedGranteeType,
                GranteeSid = isRole ? (Sid?)null : new Sid(req.RequestedGranteeRef),
                RoleName = isRole ? req.RequestedGranteeRef : null,
                ValidFrom = DateTimeOffset.UtcNow,
                ValidTo = req.RequestedValidTo,
                CreatedBySid = approverSid,
                ColumnRules = columnRules,
                RowFilters = builtRowFilters,
                Description = validatedDescription
            };

            try
            {
                // The repository re-checks separation of duties and the four-eyes requirement under the row lock and refuses a
                // step that would not complete the request (nothing is written).
                await approvalRepository.ExecuteAtomicApprovalWithConsentAsync(
                    requestId, step, consent, builtRowFilters, columnRules, ct).ConfigureAwait(false);
                approved = req;
                approved.Status = "APPROVED";
            }
            catch (ConsentRequestStatusConflictException)
            {
                throw NotPending();
            }
        }
        else
        {
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
                    ColumnRules = columnRules,
                    RowFilters = builtRowFilters,
                    Description = validatedDescription
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
        }

        var message = string.Equals(approved.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)
            ? "Consent request approved and active consent created."
            : $"Consent request approved step completed. Current status: {approved.Status}.";

        var payload = new ConsentRequestPayload
        {
            RequestId = approved.Id,
            Status = approved.Status,
            Message = message,
            Description = validatedDescription
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

        var rejectPrincipal = httpContextAccessor.HttpContext?.User;
        EnsureReviewScope(httpContextAccessor, rejectPrincipal);

        var approverSid = GetAuthenticatedUserSid(httpContextAccessor);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
                .SetMessage("A rejection reason of at least 3 characters is required.")
                .Build());
        }

        if (reason.Length > 1000)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
                .SetMessage("The rejection reason must not exceed 1000 characters.")
                .Build());
        }

        var rejectFingerprint = IdempotencyFingerprint(
            ResolveMutationTenantId(httpContextAccessor, rejectPrincipal, Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(rejectPrincipal)).Value,
            requestId, reason.Trim());
        var existing = await TryGetIdempotentAsync(approverSid, "RejectConsentRequest", idempotencyKey, rejectFingerprint, idempotencyStore, ct);
        if (existing != null)
        {
            return existing;
        }

        var principal = rejectPrincipal;

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
                .SetCode("VALIDATION_ERROR")
                .SetMessage("A revocation reason of at least 3 characters is required.")
                .Build());
        }

        if (reason.Length > 1000)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
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

        // GQL-08: the reload is a cluster-wide side effect, so a tenant-scoped GovernanceAdmin may not trigger it.
        if (!Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only cluster administrators may reload the schema.")
                .Build());
        }

        // GQL-09: publish on the channel the catalog snapshot / GraphQL type module actually listen to ("schema:reload" had no subscriber).
        await eventBus.PublishAsync(
            Autheris.Application.Catalog.ICatalogVersionProvider.CatalogChangedEventChannel,
            new Autheris.Application.Catalog.CatalogChangedEvent(0, 0, DateTimeOffset.UtcNow),
            ct);

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
        [Service] Autheris.Application.Audit.IAuditWriter? auditWriter = null,
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
        var isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!isClusterAdmin && !roles.Contains("GovernanceAdmin"))
        {
            TenantId denyTenant;
            try
            {
                denyTenant = principal.GetTenantId();
            }
            catch (System.Security.SecurityException)
            {
                denyTenant = TenantId.LegacySingleTenant;
            }

            var denyEntry = new AuditLogEntry
            {
                TenantId = denyTenant,
                EventType = AuditEventTypes.DataCatalogSync,
                ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-ANONYMOUS"),
                TargetTable = string.Empty,
                Decision = "DENY",
                TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { dryRun, reason = "ForbiddenRole" })
            };

            if (auditWriter != null)
            {
                await auditWriter.RecordRequiredAsync(denyEntry, ct).ConfigureAwait(false);
            }
            else if (auditLog != null)
            {
                await auditLog.RecordAuditEventAsync(denyEntry, ct).ConfigureAwait(false);
            }

            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may synchronize the data catalog.")
                .Build());
        }

        TenantId? restrictToTenant = null;
        if (!isClusterAdmin)
        {
            try
            {
                restrictToTenant = principal.GetTenantId();
            }
            catch (System.Security.SecurityException)
            {
                var denyEntry = new AuditLogEntry
                {
                    TenantId = TenantId.LegacySingleTenant,
                    EventType = AuditEventTypes.DataCatalogSync,
                    ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
                    TargetTable = string.Empty,
                    Decision = "DENY",
                    TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { dryRun, reason = "InvalidTenant" })
                };

                if (auditWriter != null)
                {
                    await auditWriter.RecordRequiredAsync(denyEntry, ct).ConfigureAwait(false);
                }
                else if (auditLog != null)
                {
                    await auditLog.RecordAuditEventAsync(denyEntry, ct).ConfigureAwait(false);
                }

                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage("No valid tenant in the authentication token.")
                    .Build());
            }
        }

        TenantId auditTenant = restrictToTenant ?? TenantId.LegacySingleTenant;
        var writeAheadEntry = new AuditLogEntry
        {
            TenantId = auditTenant,
            EventType = AuditEventTypes.DataCatalogSync,
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                dryRun,
                scope = restrictToTenant?.Value ?? "cluster"
            })
        };

        if (auditWriter != null)
        {
            await auditWriter.RecordRequiredAsync(writeAheadEntry, ct).ConfigureAwait(false);
        }
        else if (auditLog != null)
        {
            await auditLog.RecordAuditEventAsync(writeAheadEntry, ct).ConfigureAwait(false);
        }

        Autheris.Application.DataCatalog.Models.CatalogSyncResult result;
        try
        {
            result = await catalogSyncService.SyncCatalogAsync(dryRun, restrictToTenant, ct).ConfigureAwait(false);
        }
        catch (Autheris.Domain.Exceptions.GatewayForbiddenException)
        {
            await RecordSyncFailureAsync(auditWriter, auditLog, auditTenant, principal, dryRun, restrictToTenant, "Forbidden", ct).ConfigureAwait(false);
            throw new GraphQLException(ErrorBuilder.New().SetCode("ACCESS_DENIED").SetMessage("Access denied.").Build());
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException)
        {
            // #341 (C341-6): the cause (an egress refusal, an unreachable source) is logged here, server side only; the caller gets the
            // same fixed code as SyncOpenMetadata.
            var services = httpContextAccessor?.HttpContext?.RequestServices;
            var logger = services != null
                ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<ILogger<Mutation>>(services)
                : null;
            logger?.LogError(ex, "Data catalog synchronization failed ({ExceptionType}).", ex.GetType().Name);
            await RecordSyncFailureAsync(auditWriter, auditLog, auditTenant, principal, dryRun, restrictToTenant, "Failed", ct).ConfigureAwait(false);
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("SYNC_FAILED")
                .SetMessage("The data catalog synchronization failed.")
                .Build());
        }

        var completionEntry = new AuditLogEntry
        {
            TenantId = auditTenant,
            EventType = AuditEventTypes.DataCatalogSync,
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                dryRun,
                scope = restrictToTenant?.Value ?? "cluster",
                syncedTables = result.SyncedTablesCount,
                syncedColumns = result.SyncedColumnsCount,
                maskedColumns = result.MaskedColumnsCount,
                art9Tables = result.Art9ProtectedTablesCount
            })
        };

        if (auditWriter != null)
        {
            await auditWriter.RecordRequiredAsync(completionEntry, ct).ConfigureAwait(false);
        }
        else if (auditLog != null)
        {
            await auditLog.RecordAuditEventAsync(completionEntry, ct).ConfigureAwait(false);
        }

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

    /// <summary>#341 (C341-7): closes the write-ahead sync entry with a failure outcome; an unavailable audit sink never replaces the governed error.</summary>
    private static async Task RecordSyncFailureAsync(
        Autheris.Application.Audit.IAuditWriter? auditWriter,
        Autheris.Application.Interfaces.IAuditLogRepository? auditLog,
        TenantId tenant,
        ClaimsPrincipal principal,
        bool dryRun,
        TenantId? restrictToTenant,
        string outcome,
        CancellationToken ct)
    {
        var entry = new AuditLogEntry
        {
            TenantId = tenant,
            EventType = AuditEventTypes.DataCatalogSync,
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ERROR",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { dryRun, scope = restrictToTenant?.Value ?? "cluster", outcome })
        };

        try
        {
            if (auditWriter != null)
            {
                await auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
            }
            else if (auditLog != null)
            {
                await auditLog.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The failure entry is best effort; the write-ahead entry already exists.
        }
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
