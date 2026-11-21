using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Validation;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
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

public sealed partial class Mutation
{
    /// <summary>
    /// decision services are resolved from the request services with<c>GetRequiredService</c>; a missing registration
    /// (or missing request services) fails the call and never falls back to a permissive default.
    /// </summary>
    private static T RequireDecisionService<T>(
        IHttpContextAccessor? httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null) where T : notnull
    {
        var services = Autheris.GraphQL.Security.GraphQlCallerPrincipal.ResolveServices(resolverContext, httpContextAccessor)
            ?? throw new InvalidOperationException("Request services are unavailable; the consent decision is refused (fail closed).");
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<T>(services);
    }

    /// <summary>
    ///  (P-4): the first step of every consent request decision. Only a person may approve or reject; read-only tokens, application
    /// principals (<c>idtyp=app</c>, app id without delegated scope), certificate subjects and unauthenticated callers are refused with
    /// <c>DECISION_NOT_PERMITTED</c> before any repository access. The denial is audited per principal and minute; an audit failure never
    /// changes the answer (the recorder does not throw and is optional by design: only the guard is a hard dependency).
    /// </summary>
    private static async Task EnsureDecisionPermittedAsync(
        IHttpContextAccessor? httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var guard = RequireDecisionService<IConsentDecisionGuard>(httpContextAccessor, resolverContext);
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        var verdict = guard.Evaluate(principal);
        if (verdict.Allowed)
        {
            return;
        }

        var services = Autheris.GraphQL.Security.GraphQlCallerPrincipal.ResolveServices(resolverContext, httpContextAccessor);
        var recorder = services is not null
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<IConsentDecisionDenialRecorder>(services)
            : null;
        if (recorder != null)
        {
            await recorder.RecordAsync(principal, verdict.ReasonClass, GetContextTenantId(httpContextAccessor, resolverContext), ct).ConfigureAwait(false);
        }

        throw new GraphQLException(ErrorBuilder.New()
            .SetCode("DECISION_NOT_PERMITTED")
            .SetMessage("Only a person can approve or reject consent requests.")
            .Build());
    }

    private static IConsentDecisionPipeline ResolveDecisionPipeline(
        IHttpContextAccessor? httpContextAccessor,
        IConsentApprovalRepository approvalRepository,
        ITableMetadataRepository? metadataRepository,
        IDataOwnershipRepository ownershipRepository,
        TenantId fallbackTenantId,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null)
    {
        var decisionGuard = RequireDecisionService<IConsentDecisionGuard>(httpContextAccessor, resolverContext);
        var grantPolicy = RequireDecisionService<IGrantPolicy>(httpContextAccessor, resolverContext);
        var services = Autheris.GraphQL.Security.GraphQlCallerPrincipal.ResolveServices(resolverContext, httpContextAccessor)!;

        var registeredPipeline = services.GetService<IConsentDecisionPipeline>();
        if (registeredPipeline != null
            && ReferenceEquals(approvalRepository, services.GetService<IConsentApprovalRepository>())
            && ReferenceEquals(ownershipRepository, services.GetService<IDataOwnershipRepository>())
            && ReferenceEquals(metadataRepository, services.GetService<ITableMetadataRepository>()))
        {
            return registeredPipeline;
        }

        var grantValidityPolicy = services.GetService<IGrantValidityPolicy>()
            ?? new GrantValidityPolicy(Microsoft.Extensions.Options.Options.Create(GrantValidityOptions.CreateDefault()));
        var externalDecisionGuard = services.GetService<IExternalDecisionGuard>()
            ?? new ExternalDecisionGuard(
                services.GetService<IScimUserStore>(),
                services.GetService<IScimGroupStore>(),
                services.GetService<Autheris.Application.Identity.IIdentityRealmRegistry>(),
                services.GetService<ISeparationOfDutiesService>());

        return new ConsentDecisionPipeline(
            approvalRepository,
            metadataRepository,
            ownershipRepository,
            decisionGuard,
            grantPolicy,
            grantValidityPolicy,
            externalDecisionGuard,
            services.GetService<IConsentDecisionDenialRecorder>(),
            approvalRepository as IConsentWorkflowConfigRepository,
            services.GetService<IScimGroupStore>(),
            services.GetService<Autheris.Application.Identity.IIdentityRealmRegistry>(),
            services.GetService<IOptions<GatewayOptions>>(),
            services.GetService<IOptions<GovernanceTextOptions>>(),
            services.GetService<TimeProvider>(),
            fallbackTenantId,
            separationOfDutiesService: services.GetService<ISeparationOfDutiesService>());
    }

    private static GraphQLException ToDecisionGraphQLException(ConsentDecisionException ex)
    {
        if (string.Equals(ex.ErrorCode, ConsentDecisionErrorCodes.RequestNotDecidable, StringComparison.Ordinal))
        {
            return NotPending();
        }

        var code = string.Equals(ex.ErrorCode, ConsentDecisionErrorCodes.SelfApprovalForbidden, StringComparison.Ordinal)
            ? "FORBIDDEN"
            : ex.ErrorCode;

        return new GraphQLException(ErrorBuilder.New()
            .SetCode(code)
            .SetMessage(ex.Message)
            .Build());
    }

    internal static void EnsureReviewScope(
        IHttpContextAccessor? httpContextAccessor,
        ClaimsPrincipal? principal,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null)
    {
        var services = Autheris.GraphQL.Security.GraphQlCallerPrincipal.ResolveServices(resolverContext, httpContextAccessor);
        var gatewayOptions = services != null
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>(services)?.Value
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

    private static string? ValidateOptionalConsentDescription(
        IHttpContextAccessor? httpContextAccessor,
        string? description,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null)
    {
        if (description is null) return null;

        var services = Autheris.GraphQL.Security.GraphQlCallerPrincipal.ResolveServices(resolverContext, httpContextAccessor);
        var textOptions = services != null
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<Microsoft.Extensions.Options.IOptions<GovernanceTextOptions>>(services)
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
    /// INC1 decision 7: sets or clears the owner-authored description of a consent. Order: decision guard (a person only),
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
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        await EnsureDecisionPermittedAsync(httpContextAccessor, resolverContext, ct).ConfigureAwait(false);
        var actorSid = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);

        var isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin, resolverContext);

        var consent = await consentRepository.GetConsentByIdAsync(consentId, ct).ConfigureAwait(false);
        if (consent == null
            || (consent.TenantId != tenantId && !isCrossTenantAdmin)
            || !consent.IsActive(DateTimeOffset.UtcNow))
        {
            throw ConsentNotFound();
        }

        var isAdmin = isCrossTenantAdmin ||
            Autheris.GraphQL.Security.GraphQlCallerPrincipal.HasGatewayRole(principal, Autheris.Domain.Security.GatewayRole.GovernanceAdmin, tenantId.Value);
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
    internal static TenantId ResolveMutationTenantId(
        IHttpContextAccessor? httpContextAccessor,
        ClaimsPrincipal? principal,
        bool isCrossTenantAdmin,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null)
    {
        try
        {
            return ConsentRequestService.ResolveTenantId(principal, GetContextTenantId(httpContextAccessor, resolverContext), isCrossTenantAdmin);
        }
        catch (ConsentRequestException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetCode(ex.Code).SetMessage(ex.Message).Build());
        }
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
        => RequestTableAccessAsync(domain, schema, tableName, justification, durationDays, idempotencyKey, null, consentRequestService, httpContextAccessor, null, null, null, null, resolverContext: null, ct: ct);

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
        List<ConsentWriteRuleInput>? writeRules = null,
        bool? requestWriteOnly = null,
        List<string>? clearColumns = null,
        [Service] IDiscoveryAccessRequestService? discoveryAccessRequests = null,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var userSid = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);

        try
        {
            if (discoveryAccessRequests is not null && (writeRules is not { Count: > 0 }) && requestWriteOnly is null)
            {
                var tenant = ConsentRequestService.ResolveTenantId(
                    principal, GetContextTenantId(httpContextAccessor, resolverContext), ClusterAdminPolicy.IsCanonicalClusterAdmin(principal));
                var accessResult = await discoveryAccessRequests.RequestAsync(
                    new DiscoveryAccessRequest(domain, schema, tableName, justification, durationDays, null, clearColumns, idempotencyKey, context),
                    new CatalogCaller(principal, tenant, CatalogChannel.GraphQl),
                    ct).ConfigureAwait(false);
                return new ConsentRequestPayload { RequestId = accessResult.RequestId, Status = accessResult.Status, Message = accessResult.Message };
            }

            if (clearColumns is { Count: > 0 })
            {
                throw new ConsentRequestException("INVALID_COLUMNS", "Clear columns cannot be requested together with write rules.");
            }

            var result = await consentRequestService.SubmitAsync(
                new ConsentRequestSubmission(
                    domain, schema, tableName, justification, durationDays, idempotencyKey,
                    userSid, principal, GetContextTenantId(httpContextAccessor, resolverContext), context,
                    ConsentWriteRuleWorkflow.ToWriteRules(writeRules), requestWriteOnly),
                ct);

            return new ConsentRequestPayload
            {
                RequestId = result.RequestId,
                Status = result.Status,
                Message = result.Message
            };
        }
        catch (ConsentRequestException ex)
        {
            var error = ErrorBuilder.New().SetCode(ex.Code).SetMessage(ex.Message);
            if (ex.RetryAfterSeconds is { } retryAfter)
            {
                error.SetExtension("retryAfterSeconds", retryAfter);
            }

            throw new GraphQLException(error.Build());
        }
        catch (DiscoveryStatusException ex)
        {
            throw Query.MapDiscoveryGraphQlError(ex);
        }
    }

    public async Task<ConsentRequestPayload> WithdrawAccessRequestAsync(
        Guid requestId,
        [Service] IConsentRequestService consentRequestService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal.Identity?.IsAuthenticated != true)
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
        => ApproveConsentRequestAsync(requestId, idempotencyKey, repository, repository, repository, repository, httpContextAccessor, idempotencyStore, null, null, null, null, resolverContext: null, ct: ct);

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
            null,
            resolverContext: null,
            ct: ct);

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
        List<ConsentWriteRuleInput>? writeRules = null,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        await EnsureDecisionPermittedAsync(httpContextAccessor, resolverContext, ct).ConfigureAwait(false);
        _ = RequireDecisionService<IGrantPolicy>(httpContextAccessor, resolverContext);

        var approverSid = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        EnsureReviewScope(httpContextAccessor, principal, resolverContext);

        string? validatedDescription = ValidateOptionalConsentDescription(httpContextAccessor, description, resolverContext);
        bool isCrossTenantAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin, resolverContext);

        string rowFilterJson = rowFilter == null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(rowFilter);
        string columnMaskingJson = columnMasking?.Rules is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(columnMasking.Rules.Select(r => new
            {
                r.ColumnName,
                AccessLevel = string.Equals(r.MaskingType, "None", StringComparison.OrdinalIgnoreCase) ? "Clear" : "Mask"
            }))
            : string.Empty;

        var approveFingerprint = IdempotencyFingerprint(
            tenantId.Value,
            requestId,
            validatedDescription ?? string.Empty,
            rowFilterJson,
            columnMasking == null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(columnMasking),
            ConsentWriteRuleWorkflow.SerializeForFingerprint(writeRules));

        var existing = await TryGetIdempotentAsync(approverSid, "ApproveConsentRequest", idempotencyKey, approveFingerprint, idempotencyStore, ct);
        if (existing != null)
        {
            return existing;
        }

        var pipeline = ResolveDecisionPipeline(httpContextAccessor, approvalRepository, metadataRepository, ownershipRepository, tenantId, resolverContext);
        ConsentDecisionResult decisionResult;
        try
        {
            decisionResult = await pipeline.DecideAsync(
                new ConsentDecisionCommand(
                    RequestId: requestId,
                    Decision: ConsentDecisionKind.Approve,
                    Channel: ConsentDecisionChannel.Internal,
                    Principal: principal,
                    Description: validatedDescription,
                    WriteRules: writeRules != null ? ConsentWriteRuleWorkflow.ToWriteRules(writeRules) : null,
                    ColumnMaskingJson: string.IsNullOrEmpty(columnMaskingJson) ? null : columnMaskingJson,
                    RowFilterJson: string.IsNullOrEmpty(rowFilterJson) ? null : rowFilterJson,
                    IdempotencyKey: idempotencyKey,
                    ContextTenantId: tenantId),
                ct).ConfigureAwait(false);
        }
        catch (ConsentDecisionException ex)
        {
            throw ToDecisionGraphQLException(ex);
        }

        var approved = decisionResult.Request;
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
        => RejectConsentRequestAsync(requestId, reason, idempotencyKey, repository, repository, httpContextAccessor, idempotencyStore, resolverContext: null, ct: ct);

    public async Task<ConsentRequestPayload> RejectConsentRequestAsync(
        Guid requestId,
        string reason,
        string? idempotencyKey,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IIdempotencyStore idempotencyStore = default!,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        await EnsureDecisionPermittedAsync(httpContextAccessor, resolverContext, ct).ConfigureAwait(false);
        _ = RequireDecisionService<IGrantPolicy>(httpContextAccessor, resolverContext); // composition fail-fast: both decision resolvers need the same services

        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        EnsureReviewScope(httpContextAccessor, principal, resolverContext);

        var approverSid = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);

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

        bool isCrossTenantAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin, resolverContext);
        var rejectFingerprint = IdempotencyFingerprint(tenantId.Value, requestId, reason.Trim());
        var existing = await TryGetIdempotentAsync(approverSid, "RejectConsentRequest", idempotencyKey, rejectFingerprint, idempotencyStore, ct);
        if (existing != null)
        {
            return existing;
        }

        var pipeline = ResolveDecisionPipeline(httpContextAccessor, approvalRepository, approvalRepository as ITableMetadataRepository, ownershipRepository, tenantId, resolverContext);
        ConsentDecisionResult decisionResult;
        try
        {
            decisionResult = await pipeline.DecideAsync(
                new ConsentDecisionCommand(
                    RequestId: requestId,
                    Decision: ConsentDecisionKind.Reject,
                    Channel: ConsentDecisionChannel.Internal,
                    Principal: principal,
                    Reason: reason.Trim(),
                    IdempotencyKey: idempotencyKey,
                    ContextTenantId: tenantId),
                ct).ConfigureAwait(false);
        }
        catch (ConsentDecisionException ex)
        {
            throw ToDecisionGraphQLException(ex);
        }

        var rejected = decisionResult.Request;
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
        => RevokeConsentAsync(consentId, reason, repository, repository, httpContextAccessor, resolverContext: null, ct: ct);

    [GraphQLIgnore]
    public Task<bool> RevokeConsentAsync(
        Guid consentId,
        string reason,
        [Service] IConsentRepository consentRepository,
        [Service] IDataOwnershipRepository ownershipRepository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct)
        => RevokeConsentAsync(consentId, reason, consentRepository, ownershipRepository, httpContextAccessor, resolverContext: null, ct: ct);

    public async Task<bool> RevokeConsentAsync(
        Guid consentId,
        string reason,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var revokerSid = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);

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

        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        bool isCrossTenantAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal); // RR-L2-01: single cross-tenant definition
        var tenantId = ResolveMutationTenantId(httpContextAccessor, principal, isCrossTenantAdmin, resolverContext);

        if (consent.TenantId != tenantId && !isCrossTenantAdmin)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent '{consentId}' was not found.")
                .Build());
        }

        var authority = ConsentRevocationAuthority.FromPrincipal(principal);
        try
        {
            await consentRepository.RevokeConsentAsync(consentId, revokerSid, reason, authority, ct);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent '{consentId}' was not found.")
                .Build());
        }
        catch (ConsentRevocationForbiddenException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage(ex.Message)
                .Build());
        }

        return true;
    }
}
