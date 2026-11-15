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
            if (callerSystem is null || (externalSystemId.HasValue && externalSystemId.Value != callerSystem.Id))
            {
                return Results.Ok(new { items = Array.Empty<ConsentRequestDto>(), nextCursor = (string?)null });
            }

            effectiveExternalSystemId = callerSystem.Id;
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

        bool canRead = CanReadRequest(req, tenantId, userSid, callerSystem, isServicePrincipal, isClusterAdmin, isPrivReader);
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
        IConsentRequestService requestService,
        IConsentApprovalRepository approvalRepo,
        [FromServices] IProfileAssignmentRequestService? profileAssignmentService = null,
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

        var (domain, schema, tableName) = ResolveTableParts(body);
        var idempotencyKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var idemVals)
            ? idemVals.ToString().Trim()
            : null;
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            idempotencyKey = body.IdempotencyKey;
        }

        try
        {
            SubmitConsentRequestResult result;
            if (string.Equals(body.Kind, "profileAssignment", StringComparison.OrdinalIgnoreCase))
            {
                if (!body.ProfileId.HasValue || body.ProfileId.Value == Guid.Empty)
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "profileId is required when kind is 'profileAssignment'.", context);
                }

                if (profileAssignmentService is null)
                {
                    return ApiProblems.Problem(StatusCodes.Status501NotImplemented, ApiErrorCodes.NotImplemented, "Profile assignment requests are not enabled.", context);
                }

                result = await profileAssignmentService.SubmitAsync(
                    new ProfileAssignmentSubmission(
                        body.ProfileId.Value,
                        userSid.Value,
                        context.User,
                        tenantId,
                        body.Grantee,
                        body.GranteeType,
                        body.Justification,
                        body.RequestedDuration,
                        body.RequestedValidFrom,
                        body.RequestedValidTo,
                        body.DurationDays,
                        idempotencyKey),
                    ct).ConfigureAwait(false);
            }
            else
            {
                result = await requestService.SubmitAsync(
                    new ConsentRequestSubmission(
                        domain,
                        schema,
                        tableName,
                        body.Justification ?? string.Empty,
                        body.DurationDays ?? 30,
                        idempotencyKey,
                        userSid.Value,
                        context.User,
                        tenantId,
                        body.Context,
                        body.RequestedWriteRules,
                        body.RequestWriteOnly,
                        body.RequestedDuration,
                        body.RequestedValidFrom,
                        body.RequestedValidTo),
                    ct).ConfigureAwait(false);
            }

            var created = await approvalRepo.GetConsentRequestAsync(result.RequestId, ct).ConfigureAwait(false);
            if (created is not null)
            {
                context.Response.Headers.ETag = FormatRequestETag(created.Version);
                return Results.Created($"/api/v1/consent/requests/{created.Id:D}", ToRequestDto(created));
            }

            context.Response.Headers.ETag = FormatRequestETag(1);
            return Results.Created($"/api/v1/consent/requests/{result.RequestId:D}", new
            {
                requestId = result.RequestId,
                status = result.Status,
                message = result.Message
            });
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
        IConsentDecisionPipeline pipeline,
        IConsentApprovalRepository approvalRepo,
        IConsentWorkflowConfigRepository workflowConfigRepo,
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

        var bodyHash = ComputeBodyHash(body);
        var callerScope = isServicePrincipal ? (callerSystem?.Id.ToString("D") ?? clientId ?? "app") : (userSid?.Value ?? "user");
        var scopedKey = BuildScopedIdempotencyKey("decision", tenantId, callerScope, id, idempotencyKey);

        var (hit, replayResult) = await TryReplayIdempotentAsync(context, idempotencyStore, scopedKey, bodyHash, ct).ConfigureAwait(false);
        if (hit)
        {
            return replayResult!;
        }

        if (!TryParseIfMatchVersion(context, out var expectedVersion, out var ifMatchError))
        {
            return ifMatchError!;
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

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ExternalDecisionAttribution? attribution = null;
        if (isServicePrincipal)
        {
            var approvers = ResolveExternalApprovers(body);
            attribution = new ExternalDecisionAttribution(
                callerSystem!.Id,
                body.ExternalReference ?? existing!.ExternalReference ?? id.ToString("D"),
                decisionKind == ConsentDecisionKind.Approve ? "APPROVE" : "REJECT",
                approvers.Count > 0 ? approvers[0] : null,
                body.DecidedAt ?? now,
                body.Reason,
                body.CorrelationId,
                approvers.Count > 1 ? approvers.Skip(1).ToArray() : null);
        }

        try
        {
            var result = await pipeline.DecideAsync(
                new ConsentDecisionCommand(
                    id,
                    decisionKind,
                    isServicePrincipal ? ConsentDecisionChannel.External : ConsentDecisionChannel.Internal,
                    context.User,
                    expectedVersion,
                    body.ApprovedValidTo,
                    body.Reason,
                    body.Description,
                    body.WriteRules,
                    body.ColumnMaskingJson,
                    body.RowFilterJson,
                    idempotencyKey,
                    attribution,
                    tenantId,
                    ApprovedProfileItems: body.ApprovedItems,
                    ApprovedClearColumns: body.ApprovedClearColumns),
                ct).ConfigureAwait(false);

            var eTag = FormatRequestETag(result.Request.Version);
            context.Response.Headers.ETag = eTag;
            var responsePayload = new ConsentDecisionApiResponse(
                ToRequestDto(result.Request),
                result.ActivatedConsent is not null ? ToGrantDto(result.ActivatedConsent) : null);

            await StoreIdempotentResponseAsync(idempotencyStore, scopedKey, bodyHash, StatusCodes.Status200OK, responsePayload, eTag, ct).ConfigureAwait(false);
            return Results.Ok(responsePayload);
        }
        catch (ConsentDecisionException ex)
        {
            if (string.Equals(ex.ErrorCode, ConsentDecisionErrorCodes.ExternalSystemNotAuthorized, StringComparison.Ordinal)
                && isServicePrincipal)
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
                StatusCodes.Status412PreconditionFailed => "Consent request version does not match If-Match.",
                _ => "Consent decision request is invalid."
            };
            return ApiProblems.Problem(ex.StatusCode, mappedCode, detail, context);
        }
    }

    internal static async Task<IResult> WithdrawRequestAsync(
        Guid id,
        HttpContext context,
        IConsentRequestService requestService,
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
            var withdrawn = await requestService.WithdrawAsync(id, context.User, ct).ConfigureAwait(false);
            if (!withdrawn)
            {
                return ApiProblems.Conflict(
                    ConsentDecisionErrorCodes.RequestNotDecidable,
                    $"Consent request '{id}' can no longer be withdrawn.",
                    context);
            }

            return Results.Ok(new { requestId = id, status = "WITHDRAWN" });
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
            if (system is null)
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

    private static (string Domain, string Schema, string TableName) ResolveTableParts(CreateConsentRequestBody body)
    {
        if (!string.IsNullOrWhiteSpace(body.Domain) || !string.IsNullOrWhiteSpace(body.Schema) || !string.IsNullOrWhiteSpace(body.TableName))
        {
            return (body.Domain ?? string.Empty, body.Schema ?? string.Empty, body.TableName ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(body.Table))
        {
            var parts = body.Table.Trim().Split('.', 3, StringSplitOptions.TrimEntries);
            if (parts.Length == 3)
            {
                return (parts[0], parts[1], parts[2]);
            }
        }

        return (string.Empty, string.Empty, string.Empty);
    }

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
            "FORBIDDEN" => ApiProblems.Forbidden(ConsentDecisionErrorCodes.DecisionNotPermitted, "Consent request operation is not permitted.", context),
            "TABLE_NOT_FOUND" or "NOT_FOUND" => ApiProblems.NotFound(ApiErrorCodes.TableNotRegistered, "Target table, access profile, or consent request was not found.", context),
            "CONFLICT" or "IDEMPOTENCY_CONFLICT" => ApiProblems.Conflict(ApiErrorCodes.Conflict, "Consent request conflicts with existing state.", context),
            AccessProfileAssignmentRules.RoleSubjectUnsupported => ApiProblems.BadRequest(AccessProfileAssignmentRules.RoleSubjectUnsupported, "Access profiles cannot be assigned directly to Role subjects.", context),
            _ => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Consent request payload is invalid.", context)
        };
    }
}

public sealed record CreateConsentRequestBody(
    string? Domain = null,
    string? Schema = null,
    string? TableName = null,
    string? Table = null,
    string? Justification = null,
    int? DurationDays = null,
    string? IdempotencyKey = null,
    AccessRequestContextInput? Context = null,
    IReadOnlyList<WriteRule>? RequestedWriteRules = null,
    bool? RequestWriteOnly = null,
    string? RequestedDuration = null,
    DateTimeOffset? RequestedValidFrom = null,
    DateTimeOffset? RequestedValidTo = null,
    string? Kind = null,
    Guid? ProfileId = null,
    string? Grantee = null,
    string? GranteeType = null);

public sealed record ConsentDecisionBody(
    string? Decision = null,
    DateTimeOffset? ApprovedValidTo = null,
    string? Reason = null,
    string? Description = null,
    IReadOnlyList<WriteRule>? WriteRules = null,
    string? ColumnMaskingJson = null,
    string? RowFilterJson = null,
    string? ExternalReference = null,
    string? CorrelationId = null,
    DateTimeOffset? DecidedAt = null,
    string? ApproverSid = null,
    AssertedApprover? Approver = null,
    IReadOnlyList<AssertedApprover>? Approvers = null,
    IReadOnlyList<int>? ApprovedItems = null,
    IReadOnlyList<string>? ApprovedClearColumns = null);

public sealed record ConsentDecisionApiResponse(
    ConsentRequestDto Request,
    ConsentGrantDto? ActivatedGrant);
