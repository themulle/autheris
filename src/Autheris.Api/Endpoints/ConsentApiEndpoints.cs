namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>
/// Canonical REST endpoints for consent requests, decisions, revocations, events, and cryptographic grant proofs.
/// </summary>
public static partial class ConsentApiEndpoints
{
    private const int MaxIdempotencyKeyLength = 256;
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapConsentApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/consent")
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        MapConsentRequestEndpoints(group);
        MapConsentGrantEndpoints(group);
        MapSelfGrantEndpoints(group);

        MapDecisionEndpoints(app, group);

        return app;
    }

    internal static async Task<IResult?> EnforceRateLimitAsync(
        HttpContext context,
        IRateLimiterService? rateLimiter,
        IOptions<GovernanceOptions>? governanceOptions,
        ConsentApiRateLimitBucket bucket,
        CancellationToken ct)
    {
        if (rateLimiter is null)
        {
            return null;
        }

        var settings = governanceOptions?.Value?.ExternalApi?.RateLimits ?? new ExternalApiRateLimitSettings();
        var perMinute = bucket switch
        {
            ConsentApiRateLimitBucket.Reads => settings.ReadsPerMinute,
            ConsentApiRateLimitBucket.DecisionsAndRevokes => settings.DecisionsAndRevokesPerMinute,
            ConsentApiRateLimitBucket.Proof => settings.ProofPerMinute,
            _ => settings.ReadsPerMinute
        };

        var limit = Math.Max(1, perMinute);
        // The tenant keeps a client id that repeats across tenants (multi-tenant app, free-text Keycloak client) from sharing a budget.
        var principalKey = ResolvePrincipalBucketKey(context.User);
        var bucketKey = string.Concat("consent-api:", bucket.ToString(), ":", ResolveCaller(context).TenantId.Value, ":", principalKey);
        var rateOptions = new PostAuthSidRateLimitOptions
        {
            TokenBucketCapacity = limit,
            TokensPerSecond = Math.Max(1, limit / 60)
        };

        var verdict = await rateLimiter.CheckPostAuthSidAsync(bucketKey, rateOptions, ct).ConfigureAwait(false);
        if (verdict.Allowed)
        {
            return null;
        }

        return ApiProblems.TooManyRequests(
            ApiErrorCodes.RateLimited,
            "Consent API rate limit exceeded.",
            context,
            retryAfterSeconds: Math.Max(1, verdict.RetryAfterSeconds));
    }

    internal static (TenantId TenantId, Sid? UserSid, string? ClientId, bool IsServicePrincipal, bool IsCrossTenantAdmin, bool IsPrivReader) ResolveCaller(HttpContext context)
    {
        var user = context.User;
        var isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(user);
        var claim = TenantClaimReader.Read(user);
        var tenantId = claim.Kind == ClaimTenantKind.Valid
            ? claim.Tenant
            : context.GetSecurityContext()?.ResolvedTenant ?? TenantId.Platform;
        var isServicePrincipal = ExternalApiPermissions.IsServicePrincipal(user);
        var clientId = ExternalApiPermissions.GetClientId(user);
        var userSid = user.GetUserSid();
        var isPrivReader = isClusterAdmin
            || GatewayPolicies.HasAnyRequestRole(context, GatewayPolicies.AdminOnlyRoles)
            || GatewayPolicies.HasAnyRequestRole(context, GatewayPolicies.AuditReadRoles);
        return (tenantId, userSid, clientId, isServicePrincipal, isClusterAdmin, isPrivReader);
    }

    internal static async Task<ExternalWorkflowSystem?> ResolveAuthorizedExternalSystemAsync(
        TenantId tenantId,
        string? clientId,
        IConsentWorkflowConfigRepository workflowConfigRepo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var system = await workflowConfigRepo.GetExternalWorkflowSystemByClientIdAsync(tenantId, clientId, ct).ConfigureAwait(false);

        // Issue #1161: a system with an unrecognised role is treated like a missing one.
        return system is not null && (system.IsActiveWorkflow || system.IsActiveObserver) ? system : null;
    }

    /// <summary>
    /// Issue #1161: tenant-wide reads need an enabled observer system AND a service-principal caller holding both the
    /// <c>Autheris.Consent.Read</c> and the <c>Autheris.Consent.Observe</c> app roles. Anything else is not an authorized observer.
    /// </summary>
    internal static bool IsAuthorizedObserver(ExternalWorkflowSystem? system, ClaimsPrincipal? caller) =>
        system is { IsActiveObserver: true }
        && ExternalApiPermissions.IsServicePrincipal(caller)
        && ExternalApiPermissions.HasAppRole(caller, ExternalApiPermissions.ConsentRead)
        && ExternalApiPermissions.HasAppRole(caller, ExternalApiPermissions.ConsentObserve);

    /// <summary>Issue #1161: the refusal every acting endpoint returns to an observer system.</summary>
    internal static IResult ObserverNotPermitted(HttpContext context) =>
        ApiProblems.Problem(
            StatusCodes.Status403Forbidden,
            ConsentDecisionErrorCodes.ObserverNotPermitted,
            "An observer system cannot perform this operation.",
            context);

    internal static bool CanReadRequest(
        ConsentRequest req,
        TenantId callerTenantId,
        Sid? callerSid,
        ExternalWorkflowSystem? callerSystem,
        bool isServicePrincipal,
        bool isCrossTenantAdmin,
        bool isPrivReader,
        bool isAuthorizedObserver = false)
    {
        if (!isCrossTenantAdmin && req.TenantId != callerTenantId)
        {
            return false;
        }

        if (isServicePrincipal)
        {
            // Issue #1161: an authorized observer reads every request of its tenant; a workflow system only the requests routed to it.
            if (isAuthorizedObserver)
            {
                return callerSystem is { IsActiveObserver: true } && string.Equals(callerSystem.TenantId, req.TenantId.Value, StringComparison.Ordinal);
            }

            return callerSystem is { IsActiveWorkflow: true } && req.ExternalSystemId == callerSystem.Id;
        }

        if (isPrivReader)
        {
            return true;
        }

        return callerSid.HasValue && req.RequesterSid == callerSid.Value;
    }

    internal static bool CanReadGrant(
        Consent consent,
        TenantId callerTenantId,
        Sid? callerSid,
        bool isServicePrincipal,
        bool isCrossTenantAdmin,
        bool isPrivReader)
    {
        if (!isCrossTenantAdmin && consent.TenantId != callerTenantId)
        {
            return false;
        }

        if (isServicePrincipal || isPrivReader)
        {
            return true;
        }

        return callerSid.HasValue && consent.GranteeSid == callerSid.Value;
    }

    internal static bool TryExtractIdempotencyKey(HttpContext context, out string key, out IResult? error)
    {
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            key = string.Empty;
            error = ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.IdempotencyKeyRequired,
                "An Idempotency-Key header is required for this operation.",
                context,
                title: "Precondition Required");
            return false;
        }

        var raw = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxIdempotencyKeyLength)
        {
            key = string.Empty;
            error = ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.IdempotencyKeyRequired,
                "Idempotency-Key must be non-empty and at most 256 characters.",
                context,
                title: "Precondition Required");
            return false;
        }

        key = raw;
        error = null;
        return true;
    }

    internal static bool TryParseIfMatchVersion(HttpContext context, out int? expectedVersion, out IResult? error)
    {
        if (!context.Request.Headers.TryGetValue("If-Match", out var values))
        {
            expectedVersion = null;
            error = ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.PreconditionRequired,
                "An If-Match ETag header is required for decision updates.",
                context,
                title: "Precondition Required");
            return false;
        }

        var raw = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            expectedVersion = null;
            error = ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.PreconditionRequired,
                "If-Match header cannot be empty.",
                context,
                title: "Precondition Required");
            return false;
        }

        if (raw == "*")
        {
            expectedVersion = null;
            error = null;
            return true;
        }

        var normalized = raw;
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        if (normalized.StartsWith("r", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            expectedVersion = parsed;
            error = null;
            return true;
        }

        expectedVersion = null;
        error = ApiProblems.Problem(
            StatusCodes.Status412PreconditionFailed,
            ConsentDecisionErrorCodes.PreconditionFailed,
            "Invalid If-Match ETag format.",
            context,
            title: "Precondition Failed");
        return false;
    }

    internal static string FormatRequestETag(int version) => string.Concat("\"r", Math.Max(1, version).ToString(CultureInfo.InvariantCulture), "\"");

    internal static string ComputeBodyHash<T>(T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static string BuildScopedIdempotencyKey(string operation, TenantId tenantId, string callerScope, Guid targetId, string key) =>
        string.Concat("consent-api:", operation, ":", tenantId.Value, ":", callerScope, ":", targetId.ToString("D"), ":", key);

    internal static async Task<(bool Hit, IResult? ReplayResult)> TryReplayIdempotentAsync(
        HttpContext context,
        IIdempotencyStore? store,
        string scopedKey,
        string bodyHash,
        CancellationToken ct)
    {
        if (store is null)
        {
            return (false, null);
        }

        var existing = await store.GetAsync<StoredIdempotentApiResponse>(scopedKey, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return (false, null);
        }

        if (!string.Equals(existing.RequestBodyHash, bodyHash, StringComparison.Ordinal))
        {
            return (true, ApiProblems.Unprocessable(
                ApiErrorCodes.IdempotencyKeyReused,
                "Idempotency-Key was already used with a different request payload.",
                context));
        }

        context.Response.Headers["Idempotent-Replayed"] = "true";
        if (!string.IsNullOrWhiteSpace(existing.ETag))
        {
            context.Response.Headers.ETag = existing.ETag;
        }

        return (true, Results.Content(existing.JsonBody, existing.ContentType, Encoding.UTF8, existing.StatusCode));
    }

    internal static async Task StoreIdempotentResponseAsync<T>(
        IIdempotencyStore? store,
        string scopedKey,
        string bodyHash,
        int statusCode,
        T responsePayload,
        string? eTag,
        CancellationToken ct)
    {
        if (store is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(responsePayload, JsonOptions);
        var entry = new StoredIdempotentApiResponse(bodyHash, statusCode, "application/json", json, eTag);
        await store.SetIfNotExistsAsync(scopedKey, entry, IdempotencyTtl, ct).ConfigureAwait(false);
    }

    internal static ConsentRequestDto ToRequestDto(ConsentRequest r) =>
        ToRequestDto(r, null);

    internal static ConsentRequestDto ToRequestDto(ConsentRequest r, ApprovalEffectPreviewDto? effectPreview) =>
        new(
            r.Id,
            r.TenantId.Value,
            r.TableIdentifier.ToString(),
            r.RequesterSid.Value,
            r.RequestedGranteeType.ToString(),
            r.RequestedGranteeRef,
            r.BusinessJustification,
            r.Status,
            r.Version <= 0 ? 1 : r.Version,
            r.WorkflowMode.ToSqlString(),
            r.ExternalSystemId,
            r.ExternalReference,
            r.RequestedAt,
            r.RequestedValidFrom,
            r.RequestedValidTo,
            r.UpdatedAt,
            r.RequestKind.ToSqlString(),
            r.ProfileId,
            effectPreview);

    internal static ConsentGrantDto ToGrantDto(Consent c) =>
        new(
            c.Id,
            c.TenantId.Value,
            c.TableIdentifier.ToString(),
            c.ConsentRequestId,
            c.Effect.ToString(),
            c.GranteeType.ToString(),
            c.GranteeSid?.Value,
            c.ValidFrom,
            c.ValidTo,
            c.IsRevoked,
            c.RevokedAt,
            c.RevokeReason,
            c.GrantSource.ToSqlString(),
            c.GrantRecordSeq,
            c.ProfileAssignmentId,
            c.Description,
            c.GrantKind.ToString(),
            c.TargetColumns);

    /// <summary>
    /// The rate-limit bucket of a caller (issue #1283): a person by their SID, whatever client carries the token, so persons
    /// behind one client (a portal) never share a budget; an app-only caller, or a caller without SID, by its client id.
    /// </summary>
    private static string ResolvePrincipalBucketKey(ClaimsPrincipal principal)
    {
        if (!ExternalApiPermissions.IsServicePrincipal(principal) && principal.GetUserSid()?.Value is { Length: > 0 } sid)
        {
            return "sid:" + sid;
        }

        return "client:" + (ExternalApiPermissions.GetClientId(principal) ?? principal.Identity?.Name ?? "anonymous");
    }
}

internal enum ConsentApiRateLimitBucket
{
    Reads,
    DecisionsAndRevokes,
    Proof
}

internal sealed record StoredIdempotentApiResponse(
    string RequestBodyHash,
    int StatusCode,
    string ContentType,
    string JsonBody,
    string? ETag);

public sealed record ConsentRequestDto(
    Guid Id,
    string TenantId,
    string Table,
    string RequesterSid,
    string GranteeType,
    string GranteeRef,
    string Justification,
    string Status,
    int Version,
    string WorkflowMode,
    Guid? ExternalSystemId,
    string? ExternalReference,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RequestedValidFrom,
    DateTimeOffset RequestedValidTo,
    DateTimeOffset? UpdatedAt,
    string RequestKind,
    Guid? ProfileId,
    ApprovalEffectPreviewDto? EffectPreview = null);

public sealed record ConsentGrantDto(
    Guid Id,
    string TenantId,
    string Table,
    Guid? ConsentRequestId,
    string Effect,
    string GranteeType,
    string? GranteeSid,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    bool IsRevoked,
    DateTimeOffset? RevokedAt,
    string? RevokeReason,
    string GrantSource,
    long GrantRecordSeq,
    Guid? ProfileAssignmentId,
    string? Description,
    string GrantKind = "Data",
    IReadOnlyList<string>? TargetColumns = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GrantProvisioningDto? Provisioning = null);
