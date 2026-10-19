namespace Autheris.Api.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-8, SEC-RF107-14): REST administration of the row filter key-set cache settings. Rule level:
/// <c>/api/v1/governance/row-filters/{id}/evaluation</c>; object level (mapped in <see cref="CatalogApiEndpoints"/>):
/// <c>/api/v1/catalog/datasets/{id}/row-filter-strategy</c>. Everything is <c>GovernanceAdmin</c>; the rule level additionally requires the
/// consent's tenant to equal the caller's tenant. Unknown id and foreign tenant produce the same fixed 404 body (no id echo).
/// </summary>
public static partial class GovernanceEndpoints
{
    private static void MapRowFilterEvaluationEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/governance/row-filters/{id}/evaluation", GetRowFilterRuleEvaluationAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPut("/api/v1/governance/row-filters/{id}/evaluation", SetRowFilterRuleEvaluationAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Full, AuditEventTypes.RowFilterEvaluationChanged);
    }

    private static IResult RowFilterNotFound() => Results.NotFound(new { error = "Row filter was not found." });

    internal static IResult DatasetNotFound() => Results.NotFound(new { error = "Dataset was not found." });

    private static IResult Rejected(string reason) => Results.BadRequest(new { error = reason });

    private static object Describe(RowFilterEvaluationSettings s) => new
    {
        strategy = RowFilterEvaluationInput.Format(s.Strategy),
        durationSeconds = s.DurationSeconds,
        maxKeys = s.MaxKeys
    };

    private static object ToResponse(string level, string? id, RowFilterEvaluationView view) => new
    {
        level,
        id,
        stored = Describe(view.Stored),
        effective = new
        {
            strategy = RowFilterEvaluationInput.Format(view.Effective.Strategy),
            strategySource = view.Effective.StrategySource.ToString(),
            durationSeconds = (int)view.Effective.Duration.TotalSeconds,
            maxKeys = view.Effective.MaxKeys
        }
    };

    private const long MaxBodyBytes = 4 * 1024;

    /// <summary>
    /// Reads and strictly binds the settings body. Binding is done here, not by the framework, so every malformed payload (numeric strategy,
    /// unknown property, wrong number type, overflow, array, null) is a curated 400 and no parser text reaches the response.
    /// </summary>
    private static async Task<(RowFilterEvaluationSettings? Settings, IResult? Error)> ReadSettingsAsync(HttpContext httpContext, CancellationToken ct)
    {
        var (json, tooLarge) = await EndpointSecurity.TryReadBodyAsync(httpContext.Request, MaxBodyBytes, "The settings payload is too large.", ct).ConfigureAwait(false);
        if (json is null)
        {
            return (null, tooLarge);
        }

        RowFilterEvaluationDto? body;
        try
        {
            body = JsonSerializer.Deserialize<RowFilterEvaluationDto>(json, RowFilterEvaluationJson);
        }
        catch (JsonException)
        {
            return (null, Rejected("The settings payload is not valid: use strategy 'Exists' or 'KeySetCache' as a string, integer durationSeconds and maxKeys, and no other properties."));
        }

        if (body is null)
        {
            return (null, Rejected("A settings payload is required."));
        }

        return RowFilterEvaluationInput.TryCreate(body.Strategy, body.DurationSeconds, body.MaxKeys, out var settings, out var error)
            ? (settings, null)
            : (null, Rejected(error!));
    }

    private static readonly JsonSerializerOptions RowFilterEvaluationJson = new() { PropertyNameCaseInsensitive = true };

    // Fail closed: a change without an identifiable actor is never written (no placeholder SID in the HMAC-chained audit).
    private static Sid? ActorOf(HttpContext httpContext) => httpContext.User.GetUserSid();

    private static IResult NoActor() => Results.StatusCode(StatusCodes.Status403Forbidden);

    internal static async Task<IResult> GetRowFilterRuleEvaluationAsync(
        string id,
        [FromServices] IRowFilterEvaluationAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var ruleId))
        {
            return RowFilterNotFound();
        }

        var view = await service.GetRuleAsync(ruleId, EndpointSecurity.GetRequestTenant(httpContext), ct).ConfigureAwait(false);
        return view is null ? RowFilterNotFound() : Results.Ok(ToResponse("rule", ruleId.ToString("D"), view));
    }

    internal static async Task<IResult> SetRowFilterRuleEvaluationAsync(
        string id,
        [FromServices] IRowFilterEvaluationAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var ruleId))
        {
            return RowFilterNotFound();
        }

        var (settings, bodyError) = await ReadSettingsAsync(httpContext, ct).ConfigureAwait(false);
        if (settings is null)
        {
            return bodyError!;
        }

        if (ActorOf(httpContext) is not { } actor)
        {
            return NoActor();
        }

        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        RowFilterEvaluationChange? change;
        try
        {
            change = await service.SetRuleAsync(ruleId, tenant, settings, actor, ct).ConfigureAwait(false);
        }
        catch (RowFilterEvaluationValidationException ex)
        {
            return Rejected(ex.Reason);
        }

        if (change is null)
        {
            return RowFilterNotFound();
        }

        var view = await service.GetRuleAsync(ruleId, tenant, ct).ConfigureAwait(false);
        return view is null ? RowFilterNotFound() : Results.Ok(ToResponse("rule", ruleId.ToString("D"), view));
    }

    internal static async Task<IResult> GetDatasetRowFilterStrategyAsync(
        string id,
        [FromServices] ITableMetadataRepository metadataRepository,
        [FromServices] IRowFilterEvaluationAdminService service,
        CancellationToken ct)
    {
        var table = await DatasetIdResolver.ResolveActiveAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (table is null)
        {
            return DatasetNotFound();
        }

        var view = await service.GetObjectAsync(table.Value, ct).ConfigureAwait(false);
        return view is null ? DatasetNotFound() : Results.Ok(ToResponse("object", table.Value.ToString(), view));
    }

    internal static async Task<IResult> SetDatasetRowFilterStrategyAsync(
        string id,
        [FromServices] ITableMetadataRepository metadataRepository,
        [FromServices] IRowFilterEvaluationAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var (settings, bodyError) = await ReadSettingsAsync(httpContext, ct).ConfigureAwait(false);
        if (settings is null)
        {
            return bodyError!;
        }

        return await ChangeDatasetAsync(id, settings, metadataRepository, service, httpContext, ct).ConfigureAwait(false);
    }

    internal static Task<IResult> ClearDatasetRowFilterStrategyAsync(
        string id,
        [FromServices] ITableMetadataRepository metadataRepository,
        [FromServices] IRowFilterEvaluationAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
        => ChangeDatasetAsync(id, null, metadataRepository, service, httpContext, ct);

    private static async Task<IResult> ChangeDatasetAsync(
        string id,
        RowFilterEvaluationSettings? settings,
        ITableMetadataRepository metadataRepository,
        IRowFilterEvaluationAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (ActorOf(httpContext) is not { } actor)
        {
            return NoActor();
        }

        var table = await DatasetIdResolver.ResolveActiveAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (table is null)
        {
            return DatasetNotFound();
        }

        RowFilterEvaluationChange? change;
        try
        {
            change = await service.SetObjectAsync(table.Value, settings, actor, EndpointSecurity.GetRequestTenant(httpContext), ct).ConfigureAwait(false);
        }
        catch (RowFilterEvaluationValidationException ex)
        {
            return Rejected(ex.Reason);
        }

        if (change is null)
        {
            return DatasetNotFound();
        }

        var view = await service.GetObjectAsync(table.Value, ct).ConfigureAwait(false);
        return view is null ? DatasetNotFound() : Results.Ok(ToResponse("object", table.Value.ToString(), view));
    }
}

/// <summary>
/// Body of the row filter evaluation PUT endpoints. Unknown properties are rejected; <see cref="Strategy"/> is a string, so a JSON number
/// fails to bind, and only the exact names <c>Exists</c> and <c>KeySetCache</c> are accepted (SEC-RF107-14b). Omitted values mean "inherit".
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RowFilterEvaluationDto
{
    public string? Strategy { get; set; }
    public int? DurationSeconds { get; set; }
    public int? MaxKeys { get; set; }
}
