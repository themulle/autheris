namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>Request body of a link replacement: the complete target set of one document or application.</summary>
public sealed record DocLinkTargetBody(string? Kind, string? Domain, string? Schema, string? Name, string? Column);

public sealed record DocLinksBody(List<DocLinkTargetBody>? Targets);

public sealed record DocDiscoveryBody(bool Hidden);

/// <summary>Routes that link documents and applications to catalog objects and hide documents from discovery.</summary>
public static class DocLinkEndpoints
{
    private const string NotFoundMessage = "The requested resource was not found.";

    public static IEndpointRouteBuilder MapDocLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/v1/docs/apps/{appId}/documents/{category}/{docId}/links", async (
            string appId, string category, string docId, DocLinksBody body, HttpContext httpContext, IDocLinkService links, CancellationToken ct) =>
        {
            if (!Enum.TryParse<DocCategory>(category, true, out var parsedCategory) || !TryMap(body, out var targets))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The link request is invalid.", httpContext);
            }

            var result = await links.ReplaceDocumentLinksAsync(httpContext.User, DocEndpointAccess.BuildCaller(httpContext), appId, parsedCategory, docId, targets, ct).ConfigureAwait(false);
            return Map(result, httpContext);
        }).RequireAuthorization().WithAudit(AuditLevel.Delegated, AuditEventTypes.DocLinksChanged);

        app.MapPut("/api/v1/docs/apps/{appId}/links", async (
            string appId, DocLinksBody body, HttpContext httpContext, IDocLinkService links, CancellationToken ct) =>
        {
            if (!TryMap(body, out var targets))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The link request is invalid.", httpContext);
            }

            var result = await links.ReplaceApplicationLinksAsync(httpContext.User, DocEndpointAccess.BuildCaller(httpContext), appId, targets, ct).ConfigureAwait(false);
            return Map(result, httpContext);
        }).RequireAuthorization().WithAudit(AuditLevel.Delegated, AuditEventTypes.DocLinksChanged);

        app.MapPut("/api/v1/docs/apps/{appId}/documents/{category}/{docId}/discovery", async (
            string appId, string category, string docId, DocDiscoveryBody body, HttpContext httpContext, IDocLinkService links, CancellationToken ct) =>
        {
            if (!Enum.TryParse<DocCategory>(category, true, out var parsedCategory))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document category is invalid.", httpContext);
            }

            var result = await links.SetDocumentHiddenAsync(httpContext.User, DocEndpointAccess.BuildCaller(httpContext), appId, parsedCategory, docId, body.Hidden, ct).ConfigureAwait(false);
            return Map(result, httpContext);
        }).RequireAuthorization().WithAudit(AuditLevel.Delegated, AuditEventTypes.DocVisibilityChanged);

        return app;
    }

    private static bool TryMap(DocLinksBody? body, out List<DocLinkTarget> targets)
    {
        targets = [];
        if (body?.Targets is null) return false;
        foreach (var t in body.Targets)
        {
            if (t is null || !Enum.TryParse<DocLinkTargetKind>(t.Kind, true, out var kind) || !Enum.IsDefined(kind)) return false;
            targets.Add(new DocLinkTarget(kind, t.Domain ?? string.Empty, t.Schema ?? string.Empty, t.Name ?? string.Empty, string.IsNullOrWhiteSpace(t.Column) ? null : t.Column));
        }

        return true;
    }

    private static IResult Map(DocLinkResult result, HttpContext httpContext) => result.Outcome switch
    {
        DocLinkOutcome.Ok => Results.Ok(new { linkCount = result.LinkCount }),
        DocLinkOutcome.Unauthenticated => ApiProblems.Unauthorized(ApiErrorCodes.Unauthorized, "Authentication is required.", httpContext),
        DocLinkOutcome.Forbidden => DocEndpointAccess.Forbidden("Changing document links requires the document writer or an owner of every linked object."),
        DocLinkOutcome.InvalidRequest => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, result.Message ?? "The link request is invalid.", httpContext),
        DocLinkOutcome.Unavailable => ApiProblems.ServiceUnavailable(ApiErrorCodes.ServiceUnavailable, result.Message ?? "The operation is temporarily unavailable.", httpContext),
        _ => ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, NotFoundMessage, httpContext)
    };
}
