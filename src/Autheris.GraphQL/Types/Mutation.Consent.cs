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
            return ConsentTenantResolver.Resolve(principal, GetContextTenantId(httpContextAccessor, resolverContext), isCrossTenantAdmin);
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
                var tenant = ConsentTenantResolver.Resolve(
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
}
