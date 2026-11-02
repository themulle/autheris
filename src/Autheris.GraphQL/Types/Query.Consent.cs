using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to query effective access.")
                .Build());
        }

        if (!TableIdentifier.TryParse(table, out var tableId))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("BAD_REQUEST")
                .SetMessage($"Invalid table identifier format: '{table}'. Expected 'domain.schema.table' or 'domain.table'.")
                .Build());
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
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view access requests.")
                .Build());
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return Array.Empty<ConsentRequestDto>();
        }

        var requests = await approvalRepository.GetRequestsByRequesterSidAsync(userSid.Value, status, ct).ConfigureAwait(false);
        var dtos = requests.Select(r => new ConsentRequestDto
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
            ItsmTicketId = r.ItsmTicketId,
            ContextJson = r.ContextJson
        });

        if (after.HasValue && after.Value > 0)
        {
            dtos = dtos.Skip(after.Value);
        }
        if (first.HasValue && first.Value > 0)
        {
            dtos = dtos.Take(first.Value);
        }

        return dtos.ToList();
    }

    [GraphQLName("pendingApprovalsForMe")]
    public async Task<IReadOnlyList<ConsentRequestDto>> PendingApprovalsForMeAsync(
        int? first = null,
        int? after = null,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view pending approvals.")
                .Build());
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return Array.Empty<ConsentRequestDto>();
        }

        var requests = await approvalRepository.GetPendingRequestsForApproverAsync(userSid.Value, ct).ConfigureAwait(false);
        var dtos = requests.Select(r => new ConsentRequestDto
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
            ItsmTicketId = r.ItsmTicketId,
            ContextJson = r.ContextJson
        });

        if (after.HasValue && after.Value > 0)
        {
            dtos = dtos.Skip(after.Value);
        }
        if (first.HasValue && first.Value > 0)
        {
            dtos = dtos.Take(first.Value);
        }

        return dtos.ToList();
    }

    [GraphQLName("myActiveConsents")]
    public async Task<IReadOnlyList<ConsentDto>> MyActiveConsentsAsync(
        string? table = null,
        int? first = null,
        int? after = null,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IOptions<GovernanceTextOptions>? textOptions = null,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view active consents.")
                .Build());
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return Array.Empty<ConsentDto>();
        }

        var groupSids = principal.GetGroupSids();
        var allSubjects = groupSids.Append(userSid.Value).ToList();
        var roles = principal.GetUserRoles();
        var tenantId = principal.GetTenantId();
        var now = DateTimeOffset.UtcNow;

        IReadOnlyList<Consent> consents;
        if (!string.IsNullOrWhiteSpace(table) && TableIdentifier.TryParse(table, out var tableId))
        {
            consents = await consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, tableId, now, tenantId, ct).ConfigureAwait(false);
        }
        else
        {
            consents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(allSubjects, roles, now, tenantId, ct).ConfigureAwait(false);
        }

        var descriptionMax = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;
        var activeConsents = consents.Where(c => c.IsActive(now) && c.TenantId == tenantId).ToList();
        await Autheris.Application.Governance.ConsentDescriptionLoader.LoadAsync(consentRepository, activeConsents, ct).ConfigureAwait(false);
        var dtos = activeConsents
            .Select(c => new ConsentDto
            {
                Id = c.Id,
                TableIdentifier = c.TableIdentifier.ToString(),
                Effect = c.Effect.ToString(),
                GranteeType = c.GranteeType.ToString(),
                GranteeSid = c.GranteeSid?.Value,
                RoleName = c.RoleName,
                ValidFrom = c.ValidFrom,
                ValidTo = c.ValidTo,
                Columns = c.ColumnRules.Select(cr => cr.ColumnName).ToList(),
                Description = ConsentDescriptionText.ForOutput(c.Description, descriptionMax)
            });

        if (after.HasValue && after.Value > 0)
        {
            dtos = dtos.Skip(after.Value);
        }
        if (first.HasValue && first.Value > 0)
        {
            dtos = dtos.Take(first.Value);
        }

        return dtos.ToList();
    }

    public async Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        Guid consentId,
        [Service] ILineageImpactAnalyzerService lineageService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor, "Authentication is required for lineage and impact analyses.");

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
}
