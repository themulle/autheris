namespace Autheris.Api.Endpoints;

using Autheris.Application.Governance.Ownership;
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
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Governance.Paging;
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

        group.MapPost("/profile-assignments/{id:guid}/revoke", RevokeProfileAssignmentAsync)
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
        string? kind = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (!TryParseGrantKindFilter(kind, out var kindFilter))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Supported grant kinds: data, unmask.", context);
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
                pageSize ?? ConsentPageQuery.DefaultPageSize,
                GrantKind: kindFilter),
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

    internal static bool TryParseGrantKindFilter(string? raw, out PermissionGrantKind? kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            kind = null;
            return true;
        }

        if (string.Equals(raw.Trim(), "data", StringComparison.OrdinalIgnoreCase))
        {
            kind = PermissionGrantKind.Data;
            return true;
        }

        if (string.Equals(raw.Trim(), "unmask", StringComparison.OrdinalIgnoreCase))
        {
            kind = PermissionGrantKind.Unmask;
            return true;
        }

        kind = null;
        return false;
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
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        TimeProvider? timeProvider = null,
        [FromServices] GrantRevokeAuthority? revokeAuthority = null,
        IAuditWriter? auditWriter = null,
        IConsentDecisionDenialRecorder? denialRecorder = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, userSid, clientId, isServicePrincipal, _, _) = ResolveCaller(context);
        ExternalWorkflowSystem? callerSystem = null;
        if (isServicePrincipal)
        {
            callerSystem = await ResolveAuthorizedExternalSystemAsync(tenantId, clientId, workflowConfigRepo, ct).ConfigureAwait(false);
            if (callerSystem is null)
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Consent grant '{id}' was not found.", context);
            }

            // Issue #1161: an observer never revokes; the refusal is recorded like a refused decision.
            if (callerSystem.Role != ExternalWorkflowSystemRole.Workflow)
            {
                if (denialRecorder is not null)
                {
                    await denialRecorder.RecordAsync(context.User, ConsentDecisionDenialReasons.Observer, tenantId, ct).ConfigureAwait(false);
                }

                return ObserverNotPermitted(context);
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

        // A grant is revoked within the caller's resolved tenant only (ClusterAdmin included): a grant of another tenant is
        // indistinguishable from a missing one and is never loaded into the revoke authority check.
        var consent = await consentRepo.GetConsentByIdAsync(id, ct).ConfigureAwait(false);
        if (consent is null || consent.TenantId != tenantId)
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

            // The repository appends grant.revoked in the revocation transaction (routed to the origin request's system, plus observers).
        }
        else
        {
            if (!userSid.HasValue)
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Authenticated user SID is required.", context);
            }

            var callerContext = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
            var verified = revokeAuthority is null
                ? null
                : await revokeAuthority.AuthorizeAsync(callerContext, consent, ct).ConfigureAwait(false);
            if (verified is null)
            {
                await RecordRevokeDeniedAsync(auditWriter, consent, userSid.Value, ct).ConfigureAwait(false);
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, ConsentRevocationForbiddenException.FixedMessage, context);
            }

            try
            {
                // The verified authority carries the capacity, which the repository records in CONSENT_REVOKED.
                await consentRepo.RevokeConsentAsync(id, userSid.Value, reason, verified.Authority, ct).ConfigureAwait(false);
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

    internal static async Task<IResult> RevokeProfileAssignmentAsync(
        Guid id,
        RevokeConsentGrantBody? body,
        HttpContext context,
        IConsentApprovalRepository approvalRepo,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        [FromServices] IProfileAssignmentStore assignmentStore,
        [FromServices] IProfileAssignmentMaintenanceService profileAssignmentService,
        [FromServices] GrantRevokeAuthority? revokeAuthority = null,
        IIdempotencyStore? idempotencyStore = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        TimeProvider? timeProvider = null,
        IConsentDecisionDenialRecorder? denialRecorder = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, userSid, clientId, isServicePrincipal, _, _) = ResolveCaller(context);
        ExternalWorkflowSystem? callerSystem = null;
        if (isServicePrincipal)
        {
            callerSystem = await ResolveAuthorizedExternalSystemAsync(tenantId, clientId, workflowConfigRepo, ct).ConfigureAwait(false);
            if (callerSystem is null)
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Profile assignment '{id}' was not found.", context);
            }

            // Issue #1161: an observer never revokes; the refusal is recorded like a refused decision.
            if (callerSystem.Role != ExternalWorkflowSystemRole.Workflow)
            {
                if (denialRecorder is not null)
                {
                    await denialRecorder.RecordAsync(context.User, ConsentDecisionDenialReasons.Observer, tenantId, ct).ConfigureAwait(false);
                }

                return ObserverNotPermitted(context);
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
        var scopedKey = BuildScopedIdempotencyKey("revoke-profile-assignment", tenantId, callerScope, id, idempotencyKey);

        var (hit, replayResult) = await TryReplayIdempotentAsync(context, idempotencyStore, scopedKey, bodyHash, ct).ConfigureAwait(false);
        if (hit)
        {
            return replayResult!;
        }

        var req = await approvalRepo.GetConsentRequestAsync(id, ct).ConfigureAwait(false);
        if (req is not null && req.TenantId != tenantId)
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Profile assignment '{id}' was not found.", context);
        }

        var effectiveTenant = req?.TenantId ?? tenantId;
        if (isServicePrincipal && (req is null || req.ExternalSystemId != callerSystem!.Id))
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Profile assignment '{id}' was not found.", context);
        }

        var grants = await assignmentStore.GetConsentsByProfileAssignmentAsync(null, effectiveTenant, id, ct).ConfigureAwait(false);
        if ((req is null && grants.Count == 0) || grants.Any(g => g.TenantId != effectiveTenant))
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.GrantNotFound, $"Profile assignment '{id}' was not found.", context);
        }

        Sid actorSid;
        if (isServicePrincipal)
        {
            actorSid = new Sid(string.Concat("external-system:", callerSystem!.Id.ToString("D")));
        }
        else
        {
            if (!userSid.HasValue)
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Authenticated user SID is required.", context);
            }

            actorSid = userSid.Value;
            var callerContext = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
            var capacity = revokeAuthority is null
                ? null
                : await revokeAuthority.EvaluateProfileAssignmentAsync(callerContext, effectiveTenant, req, grants, ct).ConfigureAwait(false);
            if (capacity is null)
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Caller is not authorized to revoke this profile assignment.", context);
            }
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var revokedCount = await profileAssignmentService.RevokeAssignmentAsync(effectiveTenant, id, actorSid, reason, ct).ConfigureAwait(false);
        if (revokedCount == 0)
        {
            return ApiProblems.Conflict(ApiErrorCodes.GrantAlreadyEnded, $"Profile assignment '{id}' has no active grants left to revoke.", context);
        }

        var responsePayload = new { profileAssignmentId = id, revokedGrantCount = revokedCount, status = "REVOKED", revokedAt = now };
        await StoreIdempotentResponseAsync(idempotencyStore, scopedKey, bodyHash, StatusCodes.Status200OK, responsePayload, null, ct).ConfigureAwait(false);
        return Results.Ok(responsePayload);
    }

    private static async Task RecordRevokeDeniedAsync(IAuditWriter? auditWriter, Consent consent, Sid actor, CancellationToken ct)
    {
        if (auditWriter is null)
        {
            return;
        }

        await auditWriter.RecordBestEffortAsync(
            new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = AuditEventTypes.ConsentDenied,
                ActorSid = actor,
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "DENY",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { ConsentId = consent.Id, Operation = "REVOKE" })
            },
            ct).ConfigureAwait(false);
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
