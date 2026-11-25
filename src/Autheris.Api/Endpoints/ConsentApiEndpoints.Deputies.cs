namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>
/// Externally sourced deputy relations (issue #1126): full-state reconciliation per source, incremental upsert and delete, and the read
/// for Data Governance, the sync identity and container owners. Writes require the tenant-bound <c>DeputySync</c> right or Data
/// Governance (policy <see cref="GatewayPolicies.DeputySync"/>); IT administration has no access.
/// </summary>
public static partial class ConsentApiEndpoints
{
    private const int MaxDeputySyncBodyBytes = 4 * 1024 * 1024;

    private static void MapDeputyEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/governance/deputies", ListDeputyRelationsAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPut("/api/v1/governance/deputies/sources/{source}", ReconcileDeputyRelationsAsync)
            .RequireAuthorization(GatewayPolicies.DeputySync)
            .WithRequestBodyLimit(MaxDeputySyncBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.DeputyRelationUpdated);

        app.MapPut("/api/v1/governance/deputies/sources/{source}/relations/{externalId}", UpsertDeputyRelationAsync)
            .RequireAuthorization(GatewayPolicies.DeputySync)
            .WithAudit(AuditLevel.Full, AuditEventTypes.DeputyRelationUpdated);

        app.MapDelete("/api/v1/governance/deputies/sources/{source}/relations/{externalId}", DeleteDeputyRelationAsync)
            .RequireAuthorization(GatewayPolicies.DeputySync)
            .WithAudit(AuditLevel.Full, AuditEventTypes.DeputyRelationEnded);
    }

    internal sealed record DeputyRelationBody(string? ExternalId, string? Principal, string? Deputy, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

    internal sealed record DeputySyncBody(List<DeputyRelationBody>? Relations);

    internal static async Task<IResult> ReconcileDeputyRelationsAsync(
        string source,
        DeputySyncBody? body,
        HttpContext context,
        [FromServices] DeputyRelationService deputies,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        bool? dryRun = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (body?.Relations is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The body must carry the full relation list of the source (relations: []).", context);
        }

        var isDryRun = dryRun == true;
        if (!isDryRun && context.User.IsReadOnly())
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "A read-only token cannot reconcile deputy relations.", context);
        }

        var (caller, tenant, holdsSync) = DeputyCaller(context);
        var result = await deputies.ReconcileAsync(caller, tenant, holdsSync, source, body.Relations.Select(ToEntry).ToList(), isDryRun, ct).ConfigureAwait(false);
        return ToResponse(result, context);
    }

    internal static async Task<IResult> UpsertDeputyRelationAsync(
        string source,
        string externalId,
        DeputyRelationBody? body,
        HttpContext context,
        [FromServices] DeputyRelationService deputies,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (body is null || (body.ExternalId is not null && !string.Equals(body.ExternalId.Trim(), externalId, StringComparison.Ordinal)))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The body must describe the relation named by the route.", context);
        }

        if (context.User.IsReadOnly())
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "A read-only token cannot change deputy relations.", context);
        }

        var (caller, tenant, holdsSync) = DeputyCaller(context);
        var result = await deputies.UpsertAsync(caller, tenant, holdsSync, source, ToEntry(body with { ExternalId = externalId }), ct).ConfigureAwait(false);
        return ToResponse(result, context);
    }

    internal static async Task<IResult> DeleteDeputyRelationAsync(
        string source,
        string externalId,
        HttpContext context,
        [FromServices] DeputyRelationService deputies,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.DecisionsAndRevokes, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        if (context.User.IsReadOnly())
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "A read-only token cannot change deputy relations.", context);
        }

        var (caller, tenant, holdsSync) = DeputyCaller(context);
        var result = await deputies.DeleteAsync(caller, tenant, holdsSync, source, externalId, ct).ConfigureAwait(false);
        return ToResponse(result, context);
    }

    internal static async Task<IResult> ListDeputyRelationsAsync(
        HttpContext context,
        [FromServices] DeputyRelationService deputies,
        IRateLimiterService? rateLimiter = null,
        IOptions<GovernanceOptions>? governanceOptions = null,
        string? principal = null,
        string? deputy = null,
        string? source = null,
        bool? active = null,
        string? containerKind = null,
        Guid? containerId = null,
        CancellationToken ct = default)
    {
        if (await EnforceRateLimitAsync(context, rateLimiter, governanceOptions, ConsentApiRateLimitBucket.Reads, ct).ConfigureAwait(false) is { } limited)
        {
            return limited;
        }

        ContainerRef? container = null;
        if (containerKind is not null || containerId is not null)
        {
            if (containerKind is null || containerId is not { } id || id == Guid.Empty ||
                !ConsentWorkflowSqlMapping.TryParseContainerKind(containerKind.Replace('-', '_'), out var kind))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "containerKind and containerId must be given together.", context);
            }

            container = new ContainerRef(kind, id);
        }

        var (caller, tenant, holdsSync) = DeputyCaller(context);
        var result = await deputies.ListAsync(caller, tenant, holdsSync, new DeputyRelationQuery(principal, deputy, source, active == true, container), ct).ConfigureAwait(false);
        return ToResponse(result, context);
    }

    private static (SecurityPrincipalContext Caller, Autheris.Domain.Common.TenantId Tenant, bool HoldsSync) DeputyCaller(HttpContext context)
    {
        var (tenant, _, _, _, _, _) = ResolveCaller(context);
        var caller = context.GetSecurityContext() ?? SecurityPrincipalContext.FromPrincipal(context.User);
        // DeputySync is tenant-scoped only: the security context confers it only for the authenticated tenant.
        return (caller, tenant, caller.HasRole(GatewayRole.DeputySync));
    }

    private static DeputyRelationEntry ToEntry(DeputyRelationBody body) =>
        new(body.ExternalId, body.Principal, body.Deputy, body.ValidFrom, body.ValidTo);

    private static IResult ToResponse(DeputyRelationResult result, HttpContext context) => result.Outcome switch
    {
        DeputyRelationOutcome.Ok when result.Relations is { } relations => Results.Ok(new { items = relations.Select(ToDeputyRelationDto).ToArray() }),
        DeputyRelationOutcome.Ok => Results.Ok(new
        {
            dryRun = result.DryRun,
            created = result.Created.Select(ToDeputyRelationDto).ToArray(),
            updated = result.Updated.Select(static u => new { before = ToDeputyRelationDto(u.Before), after = ToDeputyRelationDto(u.After) }).ToArray(),
            ended = result.Ended.Select(ToDeputyRelationDto).ToArray(),
            unchanged = result.Unchanged,
            rejected = result.Rejected.Select(static r => new { externalId = r.ExternalId, code = r.Code, message = r.Message }).ToArray()
        }),
        DeputyRelationOutcome.Forbidden => ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "The caller may not read or reconcile these deputy relations.", context),
        DeputyRelationOutcome.NotFound => ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The deputy relation was not found.", context),
        DeputyRelationOutcome.Conflict => ApiProblems.Conflict(result.ErrorCode ?? DeputyRelationErrorCodes.ConcurrentChange, result.Message ?? "Concurrent change.", context),
        _ => ApiProblems.Problem(
            StatusCodes.Status422UnprocessableEntity,
            result.ErrorCode ?? ApiErrorCodes.BadRequest,
            result.Message ?? "The deputy relation request is invalid.",
            context,
            extraExtensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["rejected"] = result.Rejected.Select(static r => new { externalId = r.ExternalId, code = r.Code, message = r.Message }).ToArray()
            })
    };

    private static object ToDeputyRelationDto(DeputyRelation relation) => new
    {
        id = relation.Id,
        source = relation.Source,
        externalId = relation.ExternalId,
        principal = relation.PrincipalRef,
        deputy = relation.DeputyRef,
        validFrom = relation.ValidFrom,
        validTo = relation.ValidTo,
        createdBy = relation.CreatedBy.Value,
        createdAt = relation.CreatedAt,
        updatedBy = relation.UpdatedBy?.Value,
        updatedAt = relation.UpdatedAt
    };
}
