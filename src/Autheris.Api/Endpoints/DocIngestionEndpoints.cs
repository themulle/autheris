namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Events;
using Autheris.Domain.Audit;
using Autheris.Domain.Events;
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
                const long maxRawSize = 50 * 1024 * 1024;
                if (request.ContentLength > maxRawSize)
                {
                    return Results.BadRequest(new { error = "Bundle archive exceeds maximum permitted size of 50 MB." });
                }

                using var ms = new MemoryStream();
                await request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
                if (ms.Length > maxRawSize)
                {
                    return Results.BadRequest(new { error = "Bundle archive exceeds maximum permitted size of 50 MB." });
                }
                if (ms.Length == 0)
                {
                    return Results.BadRequest(new { error = "No documentation bundle file found in the request." });
                }

                ms.Position = 0;
                var result = await docStore.ExtractBundleAsync(appId, ms, ct).ConfigureAwait(false);
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
            IGatewayEventBus? eventBus,
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

            if (result.Success && eventBus != null)
            {
                await eventBus.PublishAsync(new DocumentDepositedEvent(
                    EventId: Guid.NewGuid().ToString("N"),
                    AppId: appId,
                    Category: category,
                    DocId: docId,
                    Title: title,
                    SourceFilePath: result.FilePath ?? string.Empty,
                    Timestamp: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            }

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

        // 5. Search documentation REST API
        app.MapGet("/api/v1/docs/search", async (
            string query,
            string? category,
            int? limit,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "Query parameter 'query' is required." });
            }

            DocCategory? parsedCategory = null;
            if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<DocCategory>(category, true, out var c))
            {
                parsedCategory = c;
            }

            var results = await searchService.SearchAsync(query, parsedCategory, limit ?? 10, ct).ConfigureAwait(false);
            return Results.Ok(new { total = results.Count, results });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 6. List documentation topics REST API
        app.MapGet("/api/v1/docs/topics", async (
            string? appId,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            var topics = await searchService.ListTopicsAsync(appId, ct).ConfigureAwait(false);
            return Results.Ok(new { total = topics.Count, topics });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 7. Lookup error code REST API
        app.MapGet("/api/v1/docs/errors/{errorCode}", async (
            string errorCode,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            var def = await searchService.LookupErrorCodeAsync(errorCode, ct).ConfigureAwait(false);
            return def != null
                ? Results.Ok(def)
                : Results.NotFound(new { error = $"Error code '{errorCode}' was not found in documentation." });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 8. Retrieve procedure instructions REST API
        app.MapGet("/api/v1/docs/procedures/{*identifier}", async (
            string identifier,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            if (!DocChunkIdentifier.TryParse(identifier, out var id) || id == null)
            {
                return Results.BadRequest(new { error = $"Invalid document identifier format '{identifier}'." });
            }

            var chunk = await searchService.GetChunkAsync(id, ct).ConfigureAwait(false);
            return chunk != null
                ? Results.Ok(chunk)
                : Results.NotFound(new { error = $"Document section '{identifier}' was not found." });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 9. Sync existing documents from filesystem storage REST API
        app.MapPost("/api/v1/docs/sync", async (
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            var count = await docStore.SyncExistingDocumentsAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { message = $"Synchronized {count} document sections into search index.", indexedSections = count });
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionBundleApplied);

        return app;
    }
}

public sealed record DepositDocumentBody(
    string? Title,
    string Content,
    string? ErrorCode = null,
    string[]? Tags = null);
