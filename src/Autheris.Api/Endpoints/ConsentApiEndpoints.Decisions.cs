namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>Decider read side (inbox, container grant overview) and catalog grant revocation.</summary>
public static partial class ConsentApiEndpoints
{
    private static void MapDecisionEndpoints(IEndpointRouteBuilder app, RouteGroupBuilder group)
    {
        group.MapGet("/inbox", GetInboxAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/catalog-grants/{id:guid}/revoke", RevokeCatalogGrantAsync)
            .RequireAuthorization(GatewayPolicies.GrantRevoke)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRevoked);

        app.MapGet("/api/v1/governance/containers/{kind}/{id:guid}/grants", GetContainerGrantsAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
    }

    internal static async Task<IResult> GetInboxAsync(
        HttpContext context,
        [FromServices] PermissionInboxService inbox,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (cursor is not null && !ConsentRequestCursor.TryDecode(cursor, out _, out _))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidCursor, "The cursor is malformed.", context);
        }

        var (tenantId, userSid, _, isServicePrincipal, _, _) = ResolveCaller(context);
        if (isServicePrincipal || !userSid.HasValue)
        {
            // Decisions are made by people; an application identity has no inbox.
            return Results.Ok(new { items = Array.Empty<object>(), nextCursor = (string?)null });
        }

        var caller = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
        var page = await inbox.GetInboxAsync(caller, tenantId, cursor, limit, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = page.Items.Select(static i => new { request = ToRequestDto(i.Request), capacity = i.Capacity.ToString() }).ToArray(),
            nextCursor = page.NextCursor
        });
    }

    internal static async Task<IResult> GetContainerGrantsAsync(
        string kind,
        Guid id,
        HttpContext context,
        [FromServices] PermissionInboxService inbox,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        string? cursor = null,
        int? pageSize = null,
        bool? includeInactive = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        Guid? after = null;
        if (cursor is not null)
        {
            if (!PageCursor.TryDecode(cursor, out var raw) || !Guid.TryParse(raw, out var afterId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidCursor, "The cursor is malformed.", context);
            }

            after = afterId;
        }

        var (tenantId, userSid, _, isServicePrincipal, _, _) = ResolveCaller(context);
        var notFound = ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "Container was not found.", context);
        if (isServicePrincipal || !userSid.HasValue || !ConsentWorkflowSqlMapping.TryParseContainerKind(kind.Replace('-', '_'), out var containerKind))
        {
            return notFound;
        }

        var caller = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
        var page = await inbox.GetContainerGrantsAsync(
            caller,
            tenantId,
            new ContainerRef(containerKind, id),
            after,
            pageSize ?? ConsentPageQuery.DefaultPageSize,
            includeInactive == true,
            ct).ConfigureAwait(false);
        if (page is null)
        {
            return notFound;
        }

        return Results.Ok(new
        {
            items = page.Items.Select(ToGrantDto).ToArray(),
            nextCursor = page.NextCursor is { } next ? PageCursor.Encode(next) : null,
            patternGrants = page.PatternGrants?.Select(ToGrantDto).ToArray(),
            patternGrantsTruncated = page.PatternGrantsTruncated,
            catalogGrants = page.CatalogGrants?.Select(ToCatalogGrantDto).ToArray(),
            catalogGrantsTruncated = page.CatalogGrantsTruncated
        });
    }

    internal static async Task<IResult> RevokeCatalogGrantAsync(
        Guid id,
        RevokeConsentGrantBody? body,
        HttpContext context,
        [FromServices] ICatalogVisibilityGrantRepository catalogGrants,
        [FromServices] GrantRevokeAuthority revokeAuthority,
        [FromServices] IAuditWriter auditWriter,
        IIdempotencyStore? idempotencyStore = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var notFound = ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Catalog grant '{id}' was not found.", context);
        var (tenantId, userSid, _, isServicePrincipal, _, _) = ResolveCaller(context);
        if (isServicePrincipal || !userSid.HasValue)
        {
            // Catalog grants are revoked by people in a decider capacity; an application identity has none.
            return notFound;
        }

        if (!TryExtractIdempotencyKey(context, out var idempotencyKey, out var keyError))
        {
            return keyError!;
        }

        var reason = body?.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 3 || reason.Length > 1000)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A revocation reason between 3 and 1000 characters is required.", context);
        }

        var bodyHash = ComputeBodyHash(new { reason });
        var scopedKey = BuildScopedIdempotencyKey("revoke-catalog-grant", tenantId, userSid.Value.Value, id, idempotencyKey);
        var (hit, replayResult) = await TryReplayIdempotentAsync(context, idempotencyStore, scopedKey, bodyHash, ct).ConfigureAwait(false);
        if (hit)
        {
            return replayResult!;
        }

        // Only the caller's resolved tenant is read: a grant of another tenant is the same as a missing one.
        var grant = await catalogGrants.GetByIdAsync(tenantId, id, ct).ConfigureAwait(false);
        if (grant is null || grant.TenantId != tenantId)
        {
            return notFound;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!grant.IsActive(now))
        {
            return ApiProblems.Conflict(ApiErrorCodes.GrantAlreadyEnded, $"Catalog grant '{id}' has already expired or been revoked.", context);
        }

        var callerContext = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
        var capacity = await revokeAuthority.EvaluateCatalogAsync(callerContext, grant, ct).ConfigureAwait(false);
        var target = $"{grant.ContainerKind.ToSqlString()}:{grant.ContainerId:D}";
        if (capacity is not { } verified)
        {
            await auditWriter.RecordBestEffortAsync(
                new AuditLogEntry
                {
                    TenantId = grant.TenantId,
                    EventType = AuditEventTypes.ConsentDenied,
                    ActorSid = userSid.Value,
                    TargetTable = target,
                    Decision = "DENY",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = JsonSerializer.Serialize(new { CatalogGrantId = grant.Id, Operation = "REVOKE", GrantKind = "CATALOG" })
                },
                ct).ConfigureAwait(false);
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, ConsentRevocationForbiddenException.FixedMessage, context);
        }

        var revoked = await catalogGrants.RevokeAsync(grant.TenantId, grant.Id, userSid.Value, reason, now, null, ct).ConfigureAwait(false);
        if (!revoked)
        {
            return ApiProblems.Conflict(ApiErrorCodes.GrantAlreadyEnded, $"Catalog grant '{id}' has already expired or been revoked.", context);
        }

        await auditWriter.RecordRequiredAsync(
            new AuditLogEntry
            {
                TenantId = grant.TenantId,
                EventType = AuditEventTypes.ConsentRevoked,
                ActorSid = userSid.Value,
                TargetTable = target,
                Decision = "REVOKED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    CatalogGrantId = grant.Id,
                    GrantKind = "CATALOG",
                    Grantee = grant.GranteeSid.Value,
                    Reason = reason,
                    Capacity = verified.ToString()
                })
            },
            ct).ConfigureAwait(false);

        var responsePayload = new { grantId = id, status = "REVOKED", revokedAt = now };
        await StoreIdempotentResponseAsync(idempotencyStore, scopedKey, bodyHash, StatusCodes.Status200OK, responsePayload, null, ct).ConfigureAwait(false);
        return Results.Ok(responsePayload);
    }

    private static object ToCatalogGrantDto(CatalogVisibilityGrant grant) => new
    {
        id = grant.Id,
        containerKind = grant.ContainerKind.ToSqlString(),
        containerId = grant.ContainerId,
        granteeType = grant.GranteeType.ToString(),
        grantee = grant.GranteeSid.Value,
        validFrom = grant.ValidFrom,
        validTo = grant.ValidTo,
        grantedBy = grant.GrantedBySid.Value,
        capacity = grant.ApproverCapacity.ToString(),
        isRevoked = grant.IsRevoked,
        revokedAt = grant.RevokedAt
    };
}
