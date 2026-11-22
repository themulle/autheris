using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Types;

public sealed partial class Query
{
    [GraphQLName("myAccess")]
    public async Task<EffectiveTableAccessDto> MyAccessAsync(
        string table,
        [Service] Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService explanationService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to query effective access.")
                .Build());
        }

        if (!TableIdentifier.TryParse(table, out var tableId))
        {
            throw InvalidTableArgument();
        }

        return await explanationService.ExplainMyAccessAsync(principal, tableId, ct).ConfigureAwait(false);
    }

    [GraphQLName("myAccessRequests")]
    public async Task<IReadOnlyList<ConsentRequestDto>> MyAccessRequestsAsync(
        string? status = null,
        int? first = null,
        int? after = null,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        [Service] IOptions<GatewayOptions>? gatewayOptions = null,
        CancellationToken ct = default)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view access requests.")
                .Build());
        }

        var tenantId = RequireCallerTenant(principal, "No valid tenant in the authentication token.");
        var (effectiveFirst, effectiveAfter) = ResolveBoundedPaging(first, after, gatewayOptions?.Value?.GraphQL);
        var userSid = principal.GetUserSid();
        if (userSid == null || effectiveFirst == 0)
        {
            return Array.Empty<ConsentRequestDto>();
        }

        var requests = await approvalRepository.GetRequestsByRequesterSidAsync(userSid.Value, tenantId, status, ct).ConfigureAwait(false);
        return requests
            .Skip(effectiveAfter)
            .Take(effectiveFirst)
            .Select(r => new ConsentRequestDto
            {
                Id = r.Id,
                TableId = r.TableId,
                TableIdentifier = r.TableIdentifier.ToString(),
                RequesterSid = r.RequesterSid.Value,
                RequestedGranteeType = r.RequestedGranteeType.ToString(),
                RequestedGranteeRef = r.RequestedGranteeRef,
                BusinessJustification = r.BusinessJustification,
                Status = r.Status,
                RequestedAt = r.RequestedAt,
                RequestedValidTo = r.RequestedValidTo,
                ContextJson = r.ContextJson
            })
            .ToList();
    }

    [GraphQLName("pendingApprovalsForMe")]
    public async Task<IReadOnlyList<ConsentRequestDto>> PendingApprovalsForMeAsync(
        int? first = null,
        int? after = null,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] Autheris.Application.Policy.Pdp.IGrantEffectPreview? effectPreview = null,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        [Service] IOptions<GatewayOptions>? gatewayOptions = null,
        CancellationToken ct = default)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view pending approvals.")
                .Build());
        }

        var tenantId = RequireCallerTenant(principal, "No valid tenant in the authentication token.");
        var (effectiveFirst, effectiveAfter) = ResolveBoundedPaging(first, after, gatewayOptions?.Value?.GraphQL);
        var userSid = principal.GetUserSid();
        if (userSid == null || effectiveFirst == 0)
        {
            return Array.Empty<ConsentRequestDto>();
        }

        var requests = await approvalRepository.GetPendingRequestsForApproverAsync(userSid.Value, tenantId, ct).ConfigureAwait(false);
        var paged = requests.Skip(effectiveAfter).Take(effectiveFirst).ToList();
        var previews = await Autheris.Application.ConsentRequests.ProfileAssignments.ApprovalEffectPreviewHelper
            .TryBuildBatchAsync(effectPreview, paged, userSid.Value, ct)
            .ConfigureAwait(false);

        var dtos = new List<ConsentRequestDto>(paged.Count);
        foreach (var r in paged)
        {
            previews.TryGetValue(r.Id, out var preview);
            dtos.Add(new ConsentRequestDto
            {
                Id = r.Id,
                TableId = r.TableId,
                TableIdentifier = r.TableIdentifier.ToString(),
                RequesterSid = r.RequesterSid.Value,
                RequestedGranteeType = r.RequestedGranteeType.ToString(),
                RequestedGranteeRef = r.RequestedGranteeRef,
                BusinessJustification = r.BusinessJustification,
                Status = r.Status,
                RequestedAt = r.RequestedAt,
                RequestedValidTo = r.RequestedValidTo,
                ContextJson = r.ContextJson,
                EffectPreview = preview
            });
        }

        return dtos;
    }

    [GraphQLName("myActiveConsents")]
    public async Task<IReadOnlyList<ConsentDto>> MyActiveConsentsAsync(
        string? table = null,
        int? first = null,
        int? after = null,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IOptions<GovernanceTextOptions>? textOptions = null,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        [Service] IOptions<GatewayOptions>? gatewayOptions = null,
        CancellationToken ct = default)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view active consents.")
                .Build());
        }

        var tenantId = RequireCallerTenant(principal, "No valid tenant in the authentication token.");
        var (effectiveFirst, effectiveAfter) = ResolveBoundedPaging(first, after, gatewayOptions?.Value?.GraphQL);
        var userSid = principal.GetUserSid();
        if (userSid == null || effectiveFirst == 0)
        {
            return Array.Empty<ConsentDto>();
        }

        var groupSids = principal.GetGroupSids();
        var allSubjects = groupSids.Append(userSid.Value).ToList();
        var roles = principal.GetUserRoles();
        var now = DateTimeOffset.UtcNow;

        IReadOnlyList<Consent> consents;
        if (!string.IsNullOrWhiteSpace(table))
        {
            if (!TableIdentifier.TryParse(table, out var tableId))
            {
                throw InvalidTableArgument();
            }

            consents = await consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, tableId, now, tenantId, ct).ConfigureAwait(false);
        }
        else
        {
            consents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(allSubjects, roles, now, tenantId, ct).ConfigureAwait(false);
        }

        var descriptionMax = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;
        var pagedConsents = consents
            .Where(c => c.IsActive(now) && c.TenantId == tenantId)
            .Skip(effectiveAfter)
            .Take(effectiveFirst)
            .ToList();
        await Autheris.Application.Governance.ConsentDescriptionLoader.LoadAsync(consentRepository, pagedConsents, ct).ConfigureAwait(false);
        return pagedConsents
            .Select(c => new ConsentDto
            {
                Id = c.Id,
                TableIdentifier = c.TableIdentifier.ToString(),
                Effect = c.Effect.ToString(),
                GranteeType = c.GranteeType.ToString(),
                GranteeSid = c.GranteeSid?.Value,
                ValidFrom = c.ValidFrom,
                ValidTo = c.ValidTo,
                Columns = c.ColumnRules.Select(cr => cr.ColumnName).ToList(),
                Description = ConsentDescriptionText.ForOutput(c.Description, descriptionMax)
            })
            .ToList();
    }

    private static (int First, int After) ResolveBoundedPaging(int? first, int? after, GraphQLOptions? gqlOptions) =>
        ResolveBoundedPageArgs(first, after, ConsentPageQuery.DefaultPageSize, ConsentPageQuery.MaxPageSize, gqlOptions);

    private static (int First, int After) ResolveBoundedPageArgs(int? first, int? after, int defaultSize, int hardMaxSize, GraphQLOptions? gqlOptions)
    {
        int maxOffset = gqlOptions?.MaxAllowedOffset >= 0 ? gqlOptions.MaxAllowedOffset : 10_000;
        int offset = after ?? 0;
        if (offset < 0 || offset > maxOffset)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("BAD_REQUEST")
                .SetMessage($"Argument 'after' must be between 0 and {maxOffset}.")
                .Build());
        }

        if (first < 0)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("BAD_REQUEST")
                .SetMessage("Argument 'first' must not be negative.")
                .Build());
        }

        int maxPageSize = Math.Min(
            hardMaxSize,
            gqlOptions?.MaxResponseRows is > 0 and var configured ? configured : hardMaxSize);
        int limit = first.HasValue
            ? Math.Min(first.Value, maxPageSize)
            : Math.Min(defaultSize, maxPageSize);
        return (limit, offset);
    }

    public async Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        Guid consentId,
        [Service] ILineageImpactAnalyzerService lineageService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor, "Authentication is required for lineage and impact analyses.", resolverContext);

        try
        {
            return await lineageService.CalculateConsentRevocationImpactAsync(
                callerContext.Tenant,
                consentId,
                callerContext,
                ct);
        }
        catch (Exception ex) when (ex is System.Collections.Generic.KeyNotFoundException or Autheris.Domain.Exceptions.GatewayForbiddenException)
        {
            // #341: an unknown consent, a foreign tenant's consent and a missing owner/admin role answer the same fixed NOT_FOUND
            // (the contract of the consent mutations); the service message names the consent id and stays in the server log.
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage("The consent was not found.")
                .Build());
        }
    }

    private static TenantId RequireCallerTenant(ClaimsPrincipal principal, string message)
    {
        try
        {
            return principal.GetTenantId();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage(message)
                .Build());
        }
    }
}
