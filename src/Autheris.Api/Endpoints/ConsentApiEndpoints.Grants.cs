namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

public static partial class ConsentApiEndpoints
{
    private static void MapConsentGrantEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/grants", ListGrantsAsync)
            .RequireAuthorization(GatewayPolicies.ConsentRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/grants/{id:guid}", GetGrantByIdAsync)
            .RequireAuthorization(GatewayPolicies.ConsentRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/grants/{id:guid}/revoke", RevokeGrantAsync)
            .RequireAuthorization(GatewayPolicies.GrantRevoke)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRevoked);

        group.MapGet("/grant-records/proof", EvaluateProofAsync)
            .RequireAuthorization(GatewayPolicies.GrantVerify)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);

        group.MapGet("/grant-records/{seq:long}", GetGrantRecordBySeqAsync)
            .RequireAuthorization(GatewayPolicies.GrantVerify)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);

        group.MapPost("/grant-records/verify", VerifyGrantRecordsRangeAsync)
            .RequireAuthorization(GatewayPolicies.GrantVerify)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);

        group.MapGet("/grant-records/keys", GetGrantRecordKeysAsync)
            .RequireAuthorization(GatewayPolicies.GrantVerify)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);
    }

    internal static async Task<IResult> ListGrantsAsync(
        HttpContext context,
        IConsentRepository consentRepo,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        TimeProvider? timeProvider = null,
        string? table = null,
        string? grantee = null,
        string? cursor = null,
        int? pageSize = null,
        bool? includeInactive = null,
        bool? clamped = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        TableIdentifier? tableFilter = null;
        if (!string.IsNullOrWhiteSpace(table))
        {
            if (!TryParseTableIdentifier(table, out var parsedTable))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidTable, "Expected table filter format 'domain.schema.table'.", context);
            }

            tableFilter = parsedTable;
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

        var (tenantId, userSid, _, isServicePrincipal, _, isPrivReader) = ResolveCaller(context);
        var effectiveGrantee = string.IsNullOrWhiteSpace(grantee) ? null : grantee;
        if (!isServicePrincipal && !isPrivReader)
        {
            if (!userSid.HasValue)
            {
                return Results.Ok(new { items = Array.Empty<ConsentGrantDto>(), nextCursor = (string?)null, totalCount = 0 });
            }

            effectiveGrantee = userSid.Value.Value;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var page = await ConsentPageReader.ReadAsync(
            consentRepo,
            new ConsentPageQuery(
                tenantId,
                tableFilter,
                null,
                effectiveGrantee,
                includeInactive == true,
                after,
                pageSize ?? ConsentPageQuery.DefaultPageSize),
            clamped == true,
            now,
            ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page.Items.Select(ToGrantDto).ToArray(),
            nextCursor = page.NextAfterId is { } nextId ? PageCursor.Encode(nextId.ToString()) : null,
            totalCount = page.Items.Count
        });
    }

    internal static async Task<IResult> GetGrantByIdAsync(
        Guid id,
        HttpContext context,
        IConsentRepository consentRepo,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, userSid, _, isServicePrincipal, isClusterAdmin, isPrivReader) = ResolveCaller(context);
        var consent = await consentRepo.GetConsentByIdAsync(id, ct).ConfigureAwait(false);
        if (consent is null || !CanReadGrant(consent, tenantId, userSid, isServicePrincipal, isClusterAdmin, isPrivReader))
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
        }

        return Results.Ok(ToGrantDto(consent));
    }

    internal static async Task<IResult> RevokeGrantAsync(
        Guid id,
        RevokeConsentGrantBody? body,
        HttpContext context,
        IConsentRepository consentRepo,
        IConsentApprovalRepository approvalRepo,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        IIdempotencyStore? idempotencyStore = null,
        IConsentEventOutbox? eventOutbox = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, userSid, clientId, isServicePrincipal, isClusterAdmin, _) = ResolveCaller(context);
        ExternalWorkflowSystem? callerSystem = null;
        if (isServicePrincipal)
        {
            callerSystem = await ResolveAuthorizedExternalSystemAsync(tenantId, clientId, workflowConfigRepo, ct).ConfigureAwait(false);
            if (callerSystem is null)
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
            }
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
        var callerScope = isServicePrincipal ? (callerSystem?.Id.ToString("D") ?? clientId ?? "app") : (userSid?.Value ?? "user");
        var scopedKey = BuildScopedIdempotencyKey("revoke", tenantId, callerScope, id, idempotencyKey);

        var (hit, replayResult) = await TryReplayIdempotentAsync(context, idempotencyStore, scopedKey, bodyHash, ct).ConfigureAwait(false);
        if (hit)
        {
            return replayResult!;
        }

        var consent = await consentRepo.GetConsentByIdAsync(id, ct).ConfigureAwait(false);
        if (consent is null || (!isClusterAdmin && consent.TenantId != tenantId))
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
        }

        if (isServicePrincipal)
        {
            if (!consent.ConsentRequestId.HasValue)
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
            }

            var originReq = await approvalRepo.GetConsentRequestAsync(consent.ConsentRequestId.Value, ct).ConfigureAwait(false);
            if (originReq is null || originReq.TenantId != consent.TenantId || originReq.ExternalSystemId != callerSystem!.Id)
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
            }
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (consent.IsRevoked || consent.ValidTo <= now)
        {
            return ApiProblems.Conflict(ApiErrorCodes.GrantAlreadyEnded, $"Consent grant '{id}' has already expired or been revoked.", context);
        }

        if (isServicePrincipal)
        {
            var actorSid = new Sid(string.Concat("external-system:", callerSystem!.Id.ToString("D")));
            var revoked = await consentRepo.RevokeSystemConsentAsync(id, consent.ConsentRequestId!.Value, actorSid, reason, ct).ConfigureAwait(false);
            if (!revoked)
            {
                return ApiProblems.Conflict(ApiErrorCodes.GrantAlreadyEnded, $"Consent grant '{id}' has already expired or been revoked.", context);
            }

            if (eventOutbox is not null)
            {
                var payload = ConsentEventPayloads.ForGrant(
                    consent,
                    ConsentWorkflowMode.External,
                    1,
                    "REVOKED",
                    now);
                await eventOutbox.AppendAsync(
                    new ConsentEventWrite(
                        consent.TenantId.Value,
                        Guid.NewGuid(),
                        ConsentEventTypes.GrantRevoked,
                        consent.GranteeSid?.Value ?? consent.RoleName ?? actorSid.Value,
                        ConsentEventPayloads.Serialize(payload),
                        now,
                        callerSystem.Id),
                    ct: ct).ConfigureAwait(false);
            }
        }
        else
        {
            if (!userSid.HasValue)
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Authenticated user SID is required.", context);
            }

            try
            {
                await consentRepo.RevokeConsentAsync(id, userSid.Value, reason, ConsentRevocationAuthority.FromPrincipal(context.User), ct).ConfigureAwait(false);
            }
            catch (ConsentRevocationForbiddenException)
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Caller is not authorized to revoke this grant.", context);
            }
            catch (System.Collections.Generic.KeyNotFoundException)
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
            }
        }

        var responsePayload = new { grantId = id, status = "REVOKED", revokedAt = now };
        await StoreIdempotentResponseAsync(idempotencyStore, scopedKey, bodyHash, StatusCodes.Status200OK, responsePayload, null, ct).ConfigureAwait(false);
        return Results.Ok(responsePayload);
    }

    internal static async Task<IResult> EvaluateProofAsync(
        HttpContext context,
        IGrantRecordExportService exportService,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        string? grantee = null,
        string? table = null,
        string? scopeHash = null,
        DateTimeOffset? at = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Proof, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, _, _, _, _, _) = ResolveCaller(context);
        var proof = await exportService.EvaluateProofAsync(tenantId, grantee, table, scopeHash, at, ct).ConfigureAwait(false);
        return Results.Ok(proof);
    }

    internal static async Task<IResult> GetGrantRecordBySeqAsync(
        long seq,
        HttpContext context,
        IGrantRecordExportService exportService,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Proof, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, _, _, _, _, _) = ResolveCaller(context);
        var record = await exportService.GetRecordBySeqAsync(tenantId, seq, ct).ConfigureAwait(false);
        return record is null
            ? ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Grant record sequence {seq} was not found.", context)
            : Results.Ok(record);
    }

    internal static async Task<IResult> VerifyGrantRecordsRangeAsync(
        GrantRecordVerifyRequest? body,
        HttpContext context,
        IGrantRecordExportService exportService,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Proof, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, _, _, _, _, _) = ResolveCaller(context);
        var result = await exportService.VerifyAsync(tenantId, body ?? new GrantRecordVerifyRequest(), ct).ConfigureAwait(false);
        return result is null
            ? ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, "No matching grant records found to verify.", context)
            : Results.Ok(result);
    }

    internal static async Task<IResult> GetGrantRecordKeysAsync(
        HttpContext context,
        IGrantRecordExportService exportService,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Proof, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var keys = exportService.GetPublicKeys();
        return Results.Ok(new { keys });
    }

    private static bool TryParseTableIdentifier(string raw, out TableIdentifier tableId)
    {
        var parts = raw.Trim().Split('.', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            tableId = default;
            return false;
        }

        return TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out tableId);
    }
}

public sealed record RevokeConsentGrantBody(string? Reason);
