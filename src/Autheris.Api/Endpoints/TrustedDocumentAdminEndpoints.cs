namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System.Text.Json;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Issue #281: runtime administration of app-scoped trusted GraphQL documents under <c>/api/admin/trusted-documents</c>. Mapped only while
/// <c>GraphQL:AppScopedTrustedDocuments:Enabled</c> is true. Every route needs the ClusterAdmin policy (401 without authentication) plus a
/// delegated scope (403 without it): <c>trusted-documents:write</c> for PUT/DELETE, read or write for GET (SEC-281-6). Route values are validated
/// with the same rules as the app header and never echoed (SEC-281-5); the PUT body is capped at 1 MiB (SEC-281-8).
/// </summary>
public static class TrustedDocumentAdminEndpoints
{
    public const string WriteScope = "trusted-documents:write";
    public const string ReadScope = "trusted-documents:read";
    public const long MaxPutBodyBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapTrustedDocumentAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/admin/trusted-documents").RequireAuthorization(GatewayPolicies.ClusterAdmin);

        group.MapPut("/{appId}/{version}", (string appId, string version, HttpContext context, TrustedDocumentAdministrationService service) =>
                PutAsync(appId, version, context, service))
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.TrustedDocumentsReplaced);
        group.MapDelete("/{appId}/{version}", (string appId, string version, HttpContext context, TrustedDocumentAdministrationService service) =>
                RetireAsync(appId, version, context, service))
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.TrustedDocumentsRetired);
        group.MapGet("/{appId}", (string appId, HttpContext context, TrustedDocumentAdministrationService service) =>
                GetAsync(appId, context, service))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        return app;
    }

    /// <summary>Routes exist only while the feature is enabled (SEC-281-6); a disabled gateway answers 404.</summary>
    public static IEndpointRouteBuilder MapTrustedDocumentAdminEndpointsWhenEnabled(this IEndpointRouteBuilder app, Autheris.Domain.Options.GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.GraphQL.AppScopedTrustedDocuments.Enabled ? app.MapTrustedDocumentAdminEndpoints() : app;
    }

    public sealed record PutRequest(string[]? Hashes, string[]? AllowedOrigins);

    internal static async Task<IResult> PutAsync(string appId, string version, HttpContext context, TrustedDocumentAdministrationService service)
    {
        if (!HasWriteAccess(context.User, out var actor))
        {
            return Forbidden();
        }

        if (!AppIdentity.IsValidAppId(appId) || !AppIdentity.IsValidVersion(version))
        {
            return Invalid();
        }

        var (body, error) = await EndpointSecurity.TryReadBodyAsync(context.Request, MaxPutBodyBytes, "The request body is too large.", context.RequestAborted).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        PutRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PutRequest>(body!, JsonOptions);
        }
        catch (JsonException)
        {
            return Invalid();
        }

        if (request is null)
        {
            return Invalid();
        }

        try
        {
            var result = await service.PutAsync(appId, version, request.Hashes, request.AllowedOrigins, actor!, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { added = result.Added, removed = result.Removed, unchanged = result.Unchanged });
        }
        catch (TrustedDocumentAdminException ex)
        {
            return ex.IsConflict ? ApiProblems.Conflict(ApiErrorCodes.Conflict, "The trusted document version conflicts with the current state.") : Invalid();
        }
    }

    internal static async Task<IResult> RetireAsync(string appId, string version, HttpContext context, TrustedDocumentAdministrationService service)
    {
        if (!HasWriteAccess(context.User, out var actor))
        {
            return Forbidden();
        }

        if (!AppIdentity.IsValidAppId(appId) || !AppIdentity.IsValidVersion(version))
        {
            return Invalid();
        }

        try
        {
            var outcome = await service.RetireAsync(appId, version, actor!, context.RequestAborted).ConfigureAwait(false);
            return outcome == TrustedDocumentRetireOutcome.NotFound ? Results.NotFound() : Results.NoContent();
        }
        catch (TrustedDocumentAdminException)
        {
            return Invalid();
        }
    }

    internal static async Task<IResult> GetAsync(string appId, HttpContext context, TrustedDocumentAdministrationService service)
    {
        if (!context.User.HasScope(WriteScope) && !context.User.HasScope(ReadScope))
        {
            return Forbidden();
        }

        if (!AppIdentity.IsValidAppId(appId))
        {
            return Invalid();
        }

        var view = await service.GetAppAsync(appId, context.RequestAborted).ConfigureAwait(false);
        if (view is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            appId = view.AppId,
            allowedOrigins = view.Origins,
            versions = view.Versions.Select(v => new
            {
                version = v.Version,
                status = v.Status,
                hashCount = v.HashCount,
                createdAt = v.CreatedAt,
                updatedAt = v.UpdatedAt,
                retiredAt = v.RetiredAt
            })
        });
    }

    private static bool HasWriteAccess(System.Security.Claims.ClaimsPrincipal user, out TrustedDocumentActor? actor)
    {
        actor = null;
        if (!user.HasScope(WriteScope) || user.GetUserSid() is not { } sid)
        {
            return false;
        }

        actor = new TrustedDocumentActor(sid.Value, WriteScope);
        return true;
    }

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private static IResult Invalid() => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The trusted document request is invalid.");
}
