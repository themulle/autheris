namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Requests;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
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
    private static void MapConsentRequestEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/requests", ListRequestsAsync)
            .RequireAuthorization(GatewayPolicies.ConsentRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/requests/{id:guid}", GetRequestByIdAsync)
            .RequireAuthorization(GatewayPolicies.ConsentRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/requests", CreateRequestAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        group.MapPost("/requests/{id:guid}/decision", DecideRequestAsync)
            .RequireAuthorization(GatewayPolicies.ConsentDecide)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        group.MapPost("/requests/{id:guid}/withdraw", WithdrawRequestAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentWithdrawn);

        group.MapGet("/events", GetEventsAsync)
            .RequireAuthorization(GatewayPolicies.ConsentRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
    }

    internal static async Task<IResult> ListRequestsAsync(
        HttpContext context,
        IConsentApprovalRepository approvalRepo,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        string? status = null,
        Guid? externalSystemId = null,
        DateTimeOffset? updatedSince = null,
        string? cursor = null,
        int limit = 50,
        string? domain = null,
        string? schema = null,
        string? table = null,
        string? requester = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, userSid, clientId, isServicePrincipal, _, isPrivReader) = ResolveCaller(context);
        Guid? effectiveExternalSystemId = externalSystemId;
        string? effectiveRequester = requester;

        if (isServicePrincipal)
        {
            var callerSystem = await ResolveAuthorizedExternalSystemAsync(tenantId, clientId, workflowConfigRepo, ct).ConfigureAwait(false);
            if (callerSystem is { Role: ExternalWorkflowSystemRole.Observer })
            {
                // Issue #1161: an authorized observer lists every request of its tenant (the externalSystemId filter is honoured).
                if (!IsAuthorizedObserver(callerSystem, context.User))
                {
                    return Results.Ok(new { items = Array.Empty<ConsentRequestDto>(), nextCursor = (string?)null });
                }
            }
            else if (callerSystem is null || (externalSystemId.HasValue && externalSystemId.Value != callerSystem.Id))
            {
                return Results.Ok(new { items = Array.Empty<ConsentRequestDto>(), nextCursor = (string?)null });
            }
            else
            {
                effectiveExternalSystemId = callerSystem.Id;
            }
        }
        else if (!isPrivReader)
        {
            if (!userSid.HasValue)
            {
                return Results.Ok(new { items = Array.Empty<ConsentRequestDto>(), nextCursor = (string?)null });
            }

            effectiveRequester = userSid.Value.Value;
        }

        var page = await approvalRepo.QueryTenantConsentRequestsAsync(
            new ConsentRequestPageQuery(
                tenantId,
                status,
                effectiveExternalSystemId,
                updatedSince,
                cursor,
                Math.Clamp(limit, 1, 200),
                domain,
                schema,
                table,
                effectiveRequester),
            ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page.Items.Select(ToRequestDto).ToArray(),
            nextCursor = page.NextCursor
        });
    }

    internal static async Task<IResult> GetRequestByIdAsync(
        Guid id,
        HttpContext context,
        IConsentApprovalRepository approvalRepo,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        [FromServices] IGrantEffectPreview? effectPreview = null,
        [FromServices] IProfileAssignmentStore? assignmentStore = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, userSid, clientId, isServicePrincipal, isClusterAdmin, isPrivReader) = ResolveCaller(context);
        var req = await approvalRepo.GetConsentRequestAsync(id, ct).ConfigureAwait(false);
        if (req is null)
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
        }

        ExternalWorkflowSystem? callerSystem = null;
        if (isServicePrincipal)
        {
            callerSystem = await ResolveAuthorizedExternalSystemAsync(req.TenantId, clientId, workflowConfigRepo, ct).ConfigureAwait(false);
        }

        bool canRead = CanReadRequest(
            req, tenantId, userSid, callerSystem, isServicePrincipal, isClusterAdmin, isPrivReader,
            isAuthorizedObserver: isServicePrincipal && IsAuthorizedObserver(callerSystem, context.User));
        if (!canRead && !isServicePrincipal && userSid.HasValue &&
            req.RequestKind == ConsentRequestKind.ProfileAssignment &&
            (isClusterAdmin || req.TenantId == tenantId) &&
            assignmentStore is not null)
        {
            var items = await assignmentStore.GetAssignmentItemsAsync(null, req.Id, ct).ConfigureAwait(false);
            canRead = items.Any(i => string.Equals(i.OwnerSid, userSid.Value.Value, StringComparison.OrdinalIgnoreCase));
        }

        if (!canRead)
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
        }

        var actorSid = userSid ?? new Sid(callerSystem?.Id.ToString("D") ?? "system");
        var preview = await ApprovalEffectPreviewHelper.TryBuildAsync(effectPreview, req, actorSid, ct).ConfigureAwait(false);
        context.Response.Headers.ETag = FormatRequestETag(req.Version);
        return Results.Ok(ToRequestDto(req, preview));
    }

    internal static async Task<IResult> CreateRequestAsync(
        CreateConsentRequestBody? body,
        HttpContext context,
        IPermissionRequestEngine engine,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (context.User.IsReadOnly() || ExternalApiPermissions.IsServicePrincipal(context.User))
        {
            return ApiProblems.Forbidden(
                ConsentDecisionErrorCodes.DecisionNotPermitted,
                "Service principals and read-only tokens cannot submit consent requests.",
                context);
        }

        if (body is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Request body is required.", context);
        }

        var (tenantId, userSid, _, _, _, _) = ResolveCaller(context);
        if (!userSid.HasValue || string.IsNullOrWhiteSpace(userSid.Value.Value))
        {
            return ApiProblems.Forbidden(ConsentDecisionErrorCodes.DecisionNotPermitted, "Authenticated user SID is required.", context);
        }

        if (!TryParseRequestKind(body.Kind, out var kind))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported request kind.", context);
        }

        if (string.Equals(body.GranteeType?.Trim(), "Role", StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblems.BadRequest(AccessProfileAssignmentRules.RoleSubjectUnsupported, "Access profiles cannot be assigned directly to Role subjects.", context);
        }

        var granteeType = GranteeType.User;
        if (!string.IsNullOrWhiteSpace(body.GranteeType) &&
            !Enum.TryParse(body.GranteeType.Trim(), ignoreCase: true, out granteeType))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported grantee type.", context);
        }

        var granteeRef = string.IsNullOrWhiteSpace(body.Grantee) ? userSid.Value.Value : body.Grantee.Trim();

        try
        {
            var result = await engine.SubmitAsync(
                new PermissionRequestSubmission(
                    context.User,
                    tenantId,
                    kind,
                    BuildTarget(body),
                    granteeType,
                    granteeRef,
                    body.Justification,
                    body.RequestedValidFrom,
                    body.RequestedValidTo,
                    body.RequestedValidTo is null && string.IsNullOrWhiteSpace(body.RequestedDuration)
                        ? body.DurationDays ?? DefaultDurationDays(context)
                        : null,
                    body.RequestedDuration,
                    body.RequestedWriteRules,
                    IncludeReadAccess: body.RequestWriteOnly != true),
                ct).ConfigureAwait(false);

            var created = result.Request;
            context.Response.Headers.ETag = FormatRequestETag(created.Version);
            return Results.Created($"/api/v1/consent/requests/{created.Id:D}", ToRequestDto(created));
        }
        catch (ConsentRequestException ex)
        {
            return MapRequestException(ex, context);
        }
    }

    internal static async Task<IResult> DecideRequestAsync(
        Guid id,
        ConsentDecisionBody? body,
        HttpContext context,
        IPermissionRequestEngine engine,
        IConsentApprovalRepository approvalRepo,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        IExternalDecisionService? externalDecisions = null,
        IIdempotencyStore? idempotencyStore = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        IConsentDecisionDenialRecorder? denialRecorder = null,
        TimeProvider? timeProvider = null,
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
                if (denialRecorder is not null)
                {
                    await denialRecorder.RecordAsync(context.User, ConsentDecisionDenialReasons.NotPerson, tenantId, ct).ConfigureAwait(false);
                }

                return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
            }

            // Issue #1161: an observer never decides, whatever app roles its service principal holds.
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

        if (body is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Decision body is required.", context);
        }

        if (!TryParseDecisionKind(body.Decision, out var decisionKind))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported decision; expected 'approve' or 'reject'.", context);
        }

        // The version the decider saw is mandatory: in the body, or as the If-Match ETag of the read.
        int expectedVersion;
        if (body.ExpectedVersion is { } bodyVersion)
        {
            if (bodyVersion <= 0)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "expectedVersion must be a positive integer.", context);
            }

            expectedVersion = bodyVersion;
        }
        else
        {
            if (!TryParseIfMatchVersion(context, out var ifMatchVersion, out var ifMatchError))
            {
                return ifMatchError!;
            }

            if (ifMatchVersion is not { } fromHeader)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "expectedVersion is required.", context);
            }

            expectedVersion = fromHeader;
        }

        var bodyHash = ComputeBodyHash(body);
        var callerScope = isServicePrincipal ? (callerSystem?.Id.ToString("D") ?? clientId ?? "app") : (userSid?.Value ?? "user");
        var scopedKey = BuildScopedIdempotencyKey("decision", tenantId, callerScope, id, idempotencyKey);

        var (hit, replayResult) = await TryReplayIdempotentAsync(context, idempotencyStore, scopedKey, bodyHash, ct).ConfigureAwait(false);
        if (hit)
        {
            return replayResult!;
        }

        var existing = await approvalRepo.GetConsentRequestAsync(id, ct).ConfigureAwait(false);
        if (isServicePrincipal && (existing is null || existing.TenantId != tenantId || existing.ExternalSystemId != callerSystem!.Id))
        {
            if (denialRecorder is not null)
            {
                await denialRecorder.RecordAsync(context.User, ConsentDecisionDenialReasons.NotPerson, tenantId, ct).ConfigureAwait(false);
            }

            return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
        }

        if (!isServicePrincipal && (existing is null || existing.TenantId != tenantId))
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
        }

        try
        {
            PermissionDecisionResult result;
            if (isServicePrincipal)
            {
                if (externalDecisions is null)
                {
                    return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
                }

                var approver = ResolveExternalApprovers(body).FirstOrDefault();
                if (approver is null || body.DecidedAt is null || string.IsNullOrWhiteSpace(body.Nonce) || string.IsNullOrWhiteSpace(body.Signature))
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "External decisions require approver, decidedAt, nonce and signature.", context);
                }

                result = await externalDecisions.DecideAsync(
                    new ExternalDecisionEnvelope(
                        id,
                        expectedVersion,
                        callerSystem!.Id,
                        body.ExternalReference ?? existing!.ExternalReference ?? string.Empty,
                        decisionKind == ConsentDecisionKind.Approve,
                        approver,
                        body.DecidedAt.Value,
                        body.Nonce,
                        body.Signature,
                        body.Reason,
                        context.User),
                    tenantId,
                    ct).ConfigureAwait(false);
            }
            else
            {
                var decider = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
                result = await engine.DecideAsync(
                    new PermissionDecisionCommand(
                        id,
                        expectedVersion,
                        decider,
                        decisionKind == ConsentDecisionKind.Approve,
                        body.ApprovedValidTo,
                        body.Reason,
                        body.AcknowledgeCoveredStricterColumns),
                    ct).ConfigureAwait(false);
            }

            var eTag = FormatRequestETag(result.Request.Version);
            context.Response.Headers.ETag = eTag;
            var grants = result.Grants.Select(ToDecisionGrantDto).ToArray();
            var responsePayload = new ConsentDecisionApiResponse(
                ToRequestDto(result.Request),
                grants.Length > 0 ? grants[0] : null,
                grants);

            await StoreIdempotentResponseAsync(idempotencyStore, scopedKey, bodyHash, StatusCodes.Status200OK, responsePayload, eTag, ct).ConfigureAwait(false);
            return Results.Ok(responsePayload);
        }
        catch (ConsentDecisionException ex)
        {
            if (isServicePrincipal && ex.ErrorCode is ConsentDecisionErrorCodes.ExternalSystemNotAuthorized or "NOT_FOUND")
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
            }

            var mappedCode = string.Equals(ex.ErrorCode, ConsentDecisionErrorCodes.ValidityExceedsRequested, StringComparison.Ordinal)
                ? ApiErrorCodes.DecisionExceedsRequest
                : ex.ErrorCode;

            var detail = ex.StatusCode switch
            {
                StatusCodes.Status403Forbidden => "Consent decision is not permitted.",
                StatusCodes.Status404NotFound => "Consent request was not found.",
                StatusCodes.Status409Conflict => "Consent request cannot be decided in its current state.",
                _ => "Consent decision request is invalid."
            };
            return ApiProblems.Problem(ex.StatusCode, mappedCode, detail, context);
        }
    }

    internal static async Task<IResult> WithdrawRequestAsync(
        Guid id,
        HttpContext context,
        IPermissionRequestEngine engine,
        IConsentApprovalRepository approvalRepo,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (context.User.IsReadOnly() || ExternalApiPermissions.IsServicePrincipal(context.User))
        {
            return ApiProblems.Forbidden(
                ConsentDecisionErrorCodes.DecisionNotPermitted,
                "Service principals and read-only tokens cannot withdraw consent requests.",
                context);
        }

        var (tenantId, userSid, _, _, _, _) = ResolveCaller(context);
        var existing = await approvalRepo.GetConsentRequestAsync(id, ct).ConfigureAwait(false);
        if (existing is null || existing.TenantId != tenantId || !userSid.HasValue || existing.RequesterSid != userSid.Value)
        {
            return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, $"Consent request '{id}' was not found.", context);
        }

        try
        {
            var withdrawn = await engine.WithdrawAsync(id, context.User, ct).ConfigureAwait(false);
            if (!withdrawn)
            {
                return ApiProblems.Conflict(
                    ConsentDecisionErrorCodes.RequestNotDecidable,
                    $"Consent request '{id}' can no longer be withdrawn.",
                    context);
            }

            return Results.Ok(new { requestId = id, status = "WITHDRAWN" });
        }
        catch (ConsentRequestException ex) when (ex.Code == "INVALID_STATE")
        {
            return ApiProblems.Conflict(
                ConsentDecisionErrorCodes.RequestNotDecidable,
                $"Consent request '{id}' can no longer be withdrawn.",
                context);
        }
        catch (ConsentRequestException ex)
        {
            return MapRequestException(ex, context);
        }
    }

    internal static async Task<IResult> GetEventsAsync(
        HttpContext context,
        IConsentEventOutbox eventOutbox,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        IOptions<ConsentEventsOptions>? eventsOptions = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        TimeProvider? timeProvider = null,
        long since = 0,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        var (tenantId, _, clientId, isServicePrincipal, _, _) = ResolveCaller(context);
        Guid? externalSystemId = null;
        if (isServicePrincipal)
        {
            var system = await ResolveAuthorizedExternalSystemAsync(tenantId, clientId, workflowConfigRepo, ct).ConfigureAwait(false);

            // Issue #1161: an observer reads its (tenant-wide) feed only with both app roles.
            if (system is null || (system.Role == ExternalWorkflowSystemRole.Observer && !IsAuthorizedObserver(system, context.User)))
            {
                return ApiProblems.NotFound(ConsentDecisionErrorCodes.RequestNotFound, "Registered external system not found for caller.", context);
            }

            externalSystemId = system.Id;
        }

        var retentionDays = Math.Max(1, eventsOptions?.Value?.RetentionDays ?? 30);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var retentionCutoff = now.AddDays(-retentionDays);

        var feed = await eventOutbox.ReadFeedAsync(
            tenantId,
            Math.Max(0L, since),
            Math.Clamp(limit, 1, 500),
            externalSystemId,
            retentionCutoff,
            ct).ConfigureAwait(false);

        if (feed.CursorExpired)
        {
            return ApiProblems.Problem(
                StatusCodes.Status410Gone,
                ApiErrorCodes.CursorExpired,
                "The requested event cursor precedes the event retention window.",
                context,
                title: "Gone");
        }

        return Results.Ok(new
        {
            events = feed.Events.Select(e => new
            {
                e.Seq,
                e.Id,
                e.Type,
                e.SubjectId,
                e.PayloadJson,
                e.CreatedAt
            }).ToArray(),
            nextSince = feed.NextSince
        });
    }

    private static bool TryParseRequestKind(string? raw, out ConsentRequestKind kind)
    {
        switch (raw?.Trim().ToUpperInvariant())
        {
            case null:
            case "":
            case "DATA":
                kind = ConsentRequestKind.Data;
                return true;
            case "UNMASK":
                kind = ConsentRequestKind.Unmask;
                return true;
            case "CATALOG":
                kind = ConsentRequestKind.Catalog;
                return true;
            case "PROFILEASSIGNMENT":
            case "PROFILE_ASSIGNMENT":
                kind = ConsentRequestKind.ProfileAssignment;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// Builds the permission target from <c>target</c> (or the flat legacy fields). An unparsable table yields a target without a table,
    /// which the engine answers exactly like an unknown or invisible one.
    /// </summary>
    private static PermissionTarget BuildTarget(CreateConsentRequestBody body)
    {
        var t = body.Target;
        var domain = t?.Domain ?? body.Domain;
        var schema = t?.Schema ?? body.Schema;
        var tableName = t?.TableName ?? body.TableName;
        var qualified = t?.Table ?? body.Table;
        if (string.IsNullOrWhiteSpace(domain) && string.IsNullOrWhiteSpace(schema) && string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(qualified))
        {
            var parts = qualified.Trim().Split('.', 3, StringSplitOptions.TrimEntries);
            if (parts.Length == 3)
            {
                (domain, schema, tableName) = (parts[0], parts[1], parts[2]);
            }
        }

        TableIdentifier? table = TableIdentifier.TryCreate(domain ?? string.Empty, schema ?? string.Empty, tableName ?? string.Empty, out var id) ? id : null;
        ContainerKind? containerKind = null;
        if (ConsentWorkflowSqlMapping.TryParseContainerKind(t?.ContainerKind?.Replace("-", "_", StringComparison.Ordinal), out var parsedKind))
        {
            containerKind = parsedKind;
        }

        return new PermissionTarget(table, t?.Columns, containerKind, t?.ContainerId, t?.ProfileId ?? body.ProfileId);
    }

    private static ConsentDecisionGrantDto ToDecisionGrantDto(GrantRef grant) =>
        new(grant.GrantId, grant.GrantKind.ToString(), grant.ValidFrom == default ? null : grant.ValidFrom, grant.ValidTo, grant.GrantRecordSeq);

    private static bool TryParseDecisionKind(string? raw, out ConsentDecisionKind kind)
    {
        if (string.Equals(raw, "approve", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "approved", StringComparison.OrdinalIgnoreCase))
        {
            kind = ConsentDecisionKind.Approve;
            return true;
        }

        if (string.Equals(raw, "reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "deny", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "denied", StringComparison.OrdinalIgnoreCase))
        {
            kind = ConsentDecisionKind.Reject;
            return true;
        }

        kind = default;
        return false;
    }

    private static IReadOnlyList<AssertedApprover> ResolveExternalApprovers(ConsentDecisionBody body)
    {
        if (body.Approvers is { Count: > 0 })
        {
            return body.Approvers;
        }

        if (body.Approver is not null)
        {
            return [body.Approver];
        }

        if (!string.IsNullOrWhiteSpace(body.ApproverSid))
        {
            return [new AssertedApprover(body.ApproverSid, ResolvedSid: body.ApproverSid)];
        }

        return Array.Empty<AssertedApprover>();
    }

    private static IResult MapRequestException(ConsentRequestException ex, HttpContext context)
    {
        return ex.Code switch
        {
            "RATE_LIMIT_EXCEEDED" => ApiProblems.TooManyRequests(ApiErrorCodes.RateLimitExceeded, "Access request rate limit exceeded. Retry later.", context, ex.RetryAfterSeconds ?? 1),
            "FORBIDDEN" => ApiProblems.Forbidden(ConsentDecisionErrorCodes.DecisionNotPermitted, "Consent request operation is not permitted.", context),
            "TARGET_NOT_WRITABLE" => ApiProblems.Forbidden(ApiErrorCodes.TargetNotWritable, "The target table does not allow the requested write operations.", context),
            "TABLE_NOT_FOUND" or "NOT_FOUND" or "TARGET_NOT_FOUND" => ApiProblems.NotFound(ApiErrorCodes.TableNotRegistered, "Target table, access profile, or consent request was not found.", context),
            "CONFLICT" or "IDEMPOTENCY_CONFLICT" => ApiProblems.Conflict(ApiErrorCodes.Conflict, "Consent request conflicts with existing state.", context),
            "ALREADY_COVERED" => ApiProblems.Conflict(
                ApiErrorCodes.AlreadyCovered,
                ex.CoveringGrantId is { } coveringGrantId
                    ? $"The requested columns are already covered by active unmask grant '{coveringGrantId:D}'."
                    : "The requested columns are already covered by an active unmask grant.",
                context,
                extraExtensions: ex.CoveringGrantId is { } gid
                    ? new Dictionary<string, object?> { ["grantId"] = gid.ToString("D") }
                    : null),
            AccessProfileAssignmentRules.RoleSubjectUnsupported => ApiProblems.BadRequest(AccessProfileAssignmentRules.RoleSubjectUnsupported, "Access profiles cannot be assigned directly to Role subjects.", context),
            _ => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Consent request payload is invalid.", context)
        };
    }

    /// <summary>The validity of a request that states no duration: the configured default of the grant validity policy (no hard-coded number).</summary>
    private static int DefaultDurationDays(HttpContext context)
    {
        var policy = context.RequestServices.GetRequiredService<Autheris.Application.Governance.Grants.IGrantValidityPolicy>();
        return (int)Math.Clamp(Math.Ceiling(policy.DefaultDuration.TotalDays), 1, int.MaxValue);
    }

}

public sealed record CreateConsentRequestTarget(
    string? Domain = null,
    string? Schema = null,
    string? TableName = null,
    string? Table = null,
    IReadOnlyList<string>? Columns = null,
    string? ContainerKind = null,
    Guid? ContainerId = null,
    Guid? ProfileId = null);

/// <summary>Submission body. It has no self-grant (or any other decision) field: self-grant status is computed by the engine from separation-of-duties facts.</summary>
public sealed record CreateConsentRequestBody(
    string? Domain = null,
    string? Schema = null,
    string? TableName = null,
    string? Table = null,
    string? Justification = null,
    int? DurationDays = null,
    IReadOnlyList<WriteRule>? RequestedWriteRules = null,
    bool? RequestWriteOnly = null,
    string? RequestedDuration = null,
    DateTimeOffset? RequestedValidFrom = null,
    DateTimeOffset? RequestedValidTo = null,
    string? Kind = null,
    Guid? ProfileId = null,
    string? Grantee = null,
    string? GranteeType = null,
    CreateConsentRequestTarget? Target = null);

/// <summary>Decision body. <c>expectedVersion</c> is the request version the decider saw; it has no self-grant field.</summary>
public sealed record ConsentDecisionBody(
    string? Decision = null,
    int? ExpectedVersion = null,
    DateTimeOffset? ApprovedValidTo = null,
    string? Reason = null,
    bool AcknowledgeCoveredStricterColumns = false,
    string? ExternalReference = null,
    string? CorrelationId = null,
    DateTimeOffset? DecidedAt = null,
    string? ApproverSid = null,
    AssertedApprover? Approver = null,
    IReadOnlyList<AssertedApprover>? Approvers = null,
    string? Nonce = null,
    string? Signature = null);

public sealed record ConsentDecisionGrantDto(
    Guid Id,
    string GrantKind,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    long GrantRecordSeq);

public sealed record ConsentDecisionApiResponse(
    ConsentRequestDto Request,
    ConsentDecisionGrantDto? ActivatedGrant,
    IReadOnlyList<ConsentDecisionGrantDto> Grants);
