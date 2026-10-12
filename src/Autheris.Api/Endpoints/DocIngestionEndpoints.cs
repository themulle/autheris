namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Headless REST Ingestion API for multi-app documentation bundles,
/// markdown assets, and manifest queries.
/// </summary>
public static class DocIngestionEndpoints
{
    public static IEndpointRouteBuilder MapDocIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. Upload ZIP documentation bundle (CI/CD Pipelines)
        app.MapPost("/api/v1/docs/apps/{appId}/bundle", async (
            string appId,
            HttpRequest request,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                // Raw stream upload
                var result = await docStore.ExtractBundleAsync(appId, request.Body, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }

            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("bundle") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No documentation bundle file found in the request." });
            }

            // Guard against oversized archives (e.g. 50 MB limit)
            if (file.Length > 50 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Bundle archive exceeds maximum permitted size of 50 MB." });
            }

            await using var stream = file.OpenReadStream();
            var extractResult = await docStore.ExtractBundleAsync(appId, stream, ct).ConfigureAwait(false);
            return extractResult.Success ? Results.Ok(extractResult) : Results.BadRequest(extractResult);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionBundleApplied);

        // 2. Deposit or update individual document
        app.MapPut("/api/v1/docs/apps/{appId}/documents/{category}/{docId}", async (
            string appId,
            string category,
            string docId,
            DepositDocumentBody body,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<DocCategory>(category, true, out var parsedCategory))
            {
                return Results.BadRequest(new { error = $"Invalid document category '{category}'." });
            }

            if (string.IsNullOrWhiteSpace(body.Content))
            {
                return Results.BadRequest(new { error = "Document content cannot be empty." });
            }

            var title = !string.IsNullOrWhiteSpace(body.Title) ? body.Title : docId;
            var depositRequest = new DepositDocRequest(
                appId,
                parsedCategory,
                title,
                body.Content,
                docId,
                body.ErrorCode,
                body.Tags);

            var result = await docStore.DepositDocumentAsync(depositRequest, ct).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionDocumentApplied);

        // 3. List all registered applications in the doc lake
        app.MapGet("/api/v1/docs/apps", async (
            string? domain,
            string? tag,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            var apps = await docStore.ListApplicationsAsync(domain, tag, ct).ConfigureAwait(false);
            return Results.Ok(apps);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 4. Retrieve application manifest
        app.MapGet("/api/v1/docs/apps/{appId}/manifest", async (
            string appId,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            var manifest = await docStore.GetManifestAsync(appId, ct).ConfigureAwait(false);
            return manifest != null
                ? Results.Ok(manifest)
                : Results.NotFound(new { error = $"Manifest for application '{appId}' was not found." });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        return app;
    }
}

public sealed record DepositDocumentBody(
    string? Title,
    string Content,
    string? ErrorCode = null,
    string[]? Tags = null);
