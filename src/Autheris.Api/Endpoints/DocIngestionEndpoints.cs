namespace Autheris.Api.Endpoints;

using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting;
using Autheris.Api.Security;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Events;
using Autheris.Domain.Audit;
using Autheris.Domain.Events;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

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
            IOptions<DocsOptions> docsOptions,
            CancellationToken ct) =>
        {
            if (!DocEndpointAccess.TryResolveDepositor(request.HttpContext, appId, out var writer, out var denied))
            {
                return denied!;
            }

            if (!DocumentRules.IsValidAppId(appId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The application identifier is invalid.", request.HttpContext);
            }

            var maxRawSize = docsOptions.Value.MaxBundleBytes;

            if (!request.HasFormContentType)
            {
                if (request.ContentLength > maxRawSize)
                {
                    return ApiProblems.PayloadTooLarge($"Bundle archive exceeds maximum permitted size of {maxRawSize / (1024 * 1024)} MB.", request.HttpContext, ApiErrorCodes.DocTooLarge);
                }

                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int bytesRead;
                long totalBytes = 0;
                while ((bytesRead = await request.Body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxRawSize)
                    {
                        return ApiProblems.PayloadTooLarge($"Bundle archive exceeds maximum permitted size of {maxRawSize / (1024 * 1024)} MB.", request.HttpContext, ApiErrorCodes.DocTooLarge);
                    }
                    await ms.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                }

                if (ms.Length == 0)
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "No documentation bundle file found in the request.", request.HttpContext);
                }

                ms.Position = 0;
                var result = await docStore.ImportBundleAsync(writer!, appId, ms, ct).ConfigureAwait(false);
                return WriteResult(result, request.HttpContext);
            }

            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("bundle") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file == null || file.Length == 0)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "No documentation bundle file found in the request.", request.HttpContext);
            }

            if (file.Length > maxRawSize)
            {
                return ApiProblems.PayloadTooLarge($"Bundle archive exceeds maximum permitted size of {maxRawSize / (1024 * 1024)} MB.", request.HttpContext, ApiErrorCodes.DocTooLarge);
            }

            await using var stream = file.OpenReadStream();
            var extractResult = await docStore.ImportBundleAsync(writer!, appId, stream, ct).ConfigureAwait(false);
            return WriteResult(extractResult, request.HttpContext);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionBundleApplied);

        // 2. Deposit or replace individual document (If-Match creates optimistic replacement)
        app.MapPut("/api/v1/docs/apps/{appId}/documents/{category}/{docId}", async (
            string appId,
            string category,
            string docId,
            DepositDocumentBody body,
            HttpContext httpContext,
            IUniversalDocStore docStore,
            IOptions<DocsOptions> docsOptions,
            IGatewayEventBus? eventBus,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<DocCategory>(category, true, out var parsedCategory))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document category is invalid.", httpContext);
            }

            if (!DocumentRules.IsValidAppId(appId) || !DocumentRules.IsValidDocId(docId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document identifier is invalid.", httpContext);
            }

            if (!string.IsNullOrWhiteSpace(body.ErrorCode) && !DocumentRules.IsValidErrorCode(body.ErrorCode))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The error code identifier is invalid.", httpContext);
            }

            if (string.IsNullOrWhiteSpace(body.Content))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.DocEmpty, "Document content cannot be empty.", httpContext);
            }

            if (Encoding.UTF8.GetByteCount(body.Content) > docsOptions.Value.MaxDocumentBytes * 2L &&
                Encoding.UTF8.GetByteCount(body.Content) > docsOptions.Value.MaxBundleBytes)
            {
                return ApiProblems.PayloadTooLarge($"Document exceeds maximum allowed size of {docsOptions.Value.MaxDocumentBytes} bytes.", httpContext, ApiErrorCodes.DocTooLarge);
            }

            if (!DocEndpointAccess.TryResolveDepositor(httpContext, appId, out var writer, out var denied))
            {
                return denied!;
            }

            var ifMatchHeader = httpContext.Request.Headers.IfMatch.ToString();
            long? expectedVersion = null;
            if (!string.IsNullOrWhiteSpace(ifMatchHeader))
            {
                if (!TryParseETagVersion(ifMatchHeader, out var parsedVersion))
                {
                    return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiErrorCodes.VersionMismatch, "Invalid If-Match ETag header.", httpContext);
                }

                expectedVersion = parsedVersion;
            }

            var title = !string.IsNullOrWhiteSpace(body.Title) ? body.Title : docId;
            var depositRequest = new DepositDocRequest(
                appId,
                parsedCategory,
                title,
                body.Content,
                docId,
                body.ErrorCode,
                body.Tags,
                writer!.TenantId,
                writer.AuthorSid);

            var result = expectedVersion.HasValue
                ? await docStore.ReplaceAsync(writer, depositRequest, expectedVersion.Value, ct).ConfigureAwait(false)
                : await docStore.CreateAsync(writer, depositRequest, ct).ConfigureAwait(false);

            if (!result.Success)
            {
                return WriteResult(result, httpContext);
            }

            if (eventBus != null)
            {
                await eventBus.PublishAsync(new DocumentDepositedEvent(
                    EventId: Guid.NewGuid().ToString("N"),
                    AppId: appId,
                    Category: category,
                    DocId: docId,
                    Title: title,
                    RelativePath: DocumentDepositedEvent.BuildRelativePath(appId, category, docId),
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    TenantId: EndpointSecurity.GetRequestTenant(httpContext).Value), ct).ConfigureAwait(false);
            }

            if (result.Version.HasValue)
            {
                httpContext.Response.Headers.ETag = $"\"{result.Version.Value}\"";
            }

            return expectedVersion.HasValue
                ? Results.Ok(result)
                : Results.Created($"/api/v1/docs/apps/{appId}/documents/{category}/{docId}", result);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionDocumentApplied);

        // 3. Soft-delete an individual document within the caller's tenant (or official for canonical ClusterAdmin)
        app.MapDelete("/api/v1/docs/apps/{appId}/documents/{category}/{docId}", async (
            string appId,
            string category,
            string docId,
            HttpContext httpContext,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<DocCategory>(category, true, out var parsedCategory))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document category is invalid.", httpContext);
            }

            if (!DocumentRules.IsValidAppId(appId) || !DocumentRules.IsValidDocId(docId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document identifier is invalid.", httpContext);
            }

            if (!DocEndpointAccess.TryResolveDepositor(httpContext, appId, out var writer, out var denied))
            {
                return denied!;
            }

            var key = new DocKey(writer!.TenantId, appId, parsedCategory, docId);
            var deleted = await docStore.DeleteAsync(writer, key, ct).ConfigureAwait(false);
            return deleted
                ? Results.NoContent()
                : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The requested document was not found.", httpContext);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocDocumentDeleted);

        // 4. Purge all documentation and quota records of a tenant (canonical ClusterAdmin only)
        app.MapDelete("/api/v1/docs/tenants/{tenantId}", async (
            string tenantId,
            HttpContext httpContext,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            if (!EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User))
            {
                return DocEndpointAccess.Forbidden("Purging tenant documentation requires a canonical ClusterAdmin role.");
            }

            if (!DocumentRules.IsValidTenantId(tenantId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The tenant identifier is invalid.", httpContext);
            }

            var deletedCount = await docStore.PurgeTenantAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new { tenantId, deletedCount });
        }).RequireAuthorization(GatewayPolicies.ClusterAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocTenantPurged);

        // 5. List all registered applications visible to caller scope
        app.MapGet("/api/v1/docs/apps", async (
            string? domain,
            string? tag,
            HttpContext httpContext,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            var apps = await docStore.ListApplicationsAsync(DocEndpointAccess.ResolveReadScope(httpContext), domain, tag, ct).ConfigureAwait(false);
            return Results.Ok(apps);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 6. Retrieve application manifest
        app.MapGet("/api/v1/docs/apps/{appId}/manifest", async (
            string appId,
            HttpContext httpContext,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            if (!DocumentRules.IsValidAppId(appId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The application identifier is invalid.", httpContext);
            }

            var manifest = await docStore.GetManifestAsync(DocEndpointAccess.ResolveReadScope(httpContext), appId, ct).ConfigureAwait(false);
            return manifest != null
                ? Results.Ok(manifest)
                : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The application manifest was not found.", httpContext);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 7. Search documentation REST API
        app.MapGet("/api/v1/docs/search", async (
            string query,
            string? category,
            int? limit,
            HttpContext httpContext,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Query parameter 'query' is required.", httpContext);
            }

            DocCategory? parsedCategory = null;
            if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<DocCategory>(category, true, out var c))
            {
                parsedCategory = c;
            }

            var results = await searchService.SearchAsync(query, DocEndpointAccess.ResolveReadScope(httpContext), parsedCategory, limit ?? 10, ct).ConfigureAwait(false);
            return Results.Ok(new { total = results.Count, results });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 8. List documentation topics REST API
        app.MapGet("/api/v1/docs/topics", async (
            string? appId,
            HttpContext httpContext,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            var topics = await searchService.ListTopicsAsync(DocEndpointAccess.ResolveReadScope(httpContext), appId, ct).ConfigureAwait(false);
            return Results.Ok(new { total = topics.Count, topics });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 9. Lookup error code REST API
        app.MapGet("/api/v1/docs/errors/{errorCode}", async (
            string errorCode,
            HttpContext httpContext,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            var def = await searchService.LookupErrorCodeAsync(errorCode, DocEndpointAccess.ResolveReadScope(httpContext), ct).ConfigureAwait(false);
            return def != null
                ? Results.Ok(def)
                : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The error code was not found in documentation.", httpContext);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 10. Retrieve procedure instructions REST API
        app.MapGet("/api/v1/docs/procedures/{*identifier}", async (
            string identifier,
            HttpContext httpContext,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            if (!DocChunkIdentifier.TryParse(identifier, out var id) || id == null)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document identifier format is invalid.", httpContext);
            }

            var chunk = await searchService.GetChunkAsync(id, DocEndpointAccess.ResolveReadScope(httpContext), ct).ConfigureAwait(false);
            return chunk != null
                ? Results.Ok(chunk)
                : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The document section was not found.", httpContext);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 11. Refresh search index from persisted store (canonical ClusterAdmin only)
        app.MapPost("/api/v1/docs/sync", async (
            HttpContext httpContext,
            IUniversalDocStore docStore,
            CancellationToken ct) =>
        {
            if (!EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User))
            {
                return DocEndpointAccess.Forbidden("Synchronizing the documentation index requires a canonical ClusterAdmin role.");
            }

            var count = await docStore.RefreshIndexAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { message = $"Synchronized {count} document sections into search index.", indexedSections = count });
        }).RequireAuthorization(GatewayPolicies.ClusterAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionBundleApplied);

        return app;
    }

    private static IResult WriteResult(DepositDocResult result, HttpContext httpContext)
    {
        if (result.Success)
        {
            return Results.Ok(result);
        }

        return result.ErrorCode switch
        {
            "FORBIDDEN" => DocEndpointAccess.Forbidden(result.Message),
            "DOC_ID_RESERVED" => ApiProblems.Conflict(ApiErrorCodes.DocIdReserved, result.Message, httpContext),
            "DOC_EXISTS" => ApiProblems.Conflict(ApiErrorCodes.DocExists, result.Message, httpContext),
            "DOC_QUOTA_EXCEEDED" => ApiProblems.Conflict(ApiErrorCodes.DocQuotaExceeded, result.Message, httpContext),
            "VERSION_MISMATCH" => ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiErrorCodes.VersionMismatch, result.Message, httpContext),
            "DOC_NOT_FOUND" => ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The requested document was not found.", httpContext),
            "DOC_TOO_LARGE" => ApiProblems.PayloadTooLarge(result.Message, httpContext, ApiErrorCodes.DocTooLarge),
            "DOC_EMPTY" => ApiProblems.BadRequest(ApiErrorCodes.DocEmpty, result.Message, httpContext),
            "ZIP_BOMB_DETECTED" => ApiProblems.BadRequest(ApiErrorCodes.ZipBombDetected, result.Message, httpContext),
            "ZIP_SLIP_ATTEMPT" => ApiProblems.BadRequest(ApiErrorCodes.ZipSlipAttempt, result.Message, httpContext),
            _ => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, result.Message, httpContext)
        };
    }

    private static bool TryParseETagVersion(string rawHeader, out long version)
    {
        version = 0;
        var span = rawHeader.AsSpan().Trim();
        if (span.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..].Trim();
        }

        if (span.Length >= 2 && span[0] == '"' && span[^1] == '"')
        {
            span = span[1..^1].Trim();
        }

        return long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) && version > 0;
    }
}

/// <summary>
/// Tenant scoping of the REST documentation endpoints, mirroring the MCP documentation tools.
/// </summary>
internal static class DocEndpointAccess
{
    public static bool TryResolveDepositor(HttpContext httpContext, string appId, out DocWriter? writer, out IResult? denied)
    {
        writer = null;
        denied = null;

        try
        {
            var authorSid = EndpointSecurity.GetCallerIdentity(httpContext);

            string? tenant = null;
            try
            {
                tenant = EndpointSecurity.GetRequestTenant(httpContext).Value;
            }
            catch (Exception ex) when (ex is SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
            {
            }

            var caller = new DocCaller(
                IsCanonicalClusterAdmin: EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User),
                IsGovernanceAdmin: GatewayPolicies.HasRole(httpContext.User, GatewayRole.GovernanceAdmin),
                TenantId: tenant,
                AuthorSid: authorSid,
                HasSession: httpContext.User.Identity?.IsAuthenticated ?? false,
                IsReadOnlySession: false);

            writer = DocAccessPolicy.ResolveWriter(caller, appId, out var policyDenial);
            if (writer is null)
            {
                denied = Forbidden(policyDenial.Reason);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            denied = Forbidden("Depositing documentation requires a tenant-bound session.");
            return false;
        }
    }

    public static bool TryResolveDepositor(HttpContext httpContext, string appId, out string? tenantId, out string? authorSid, out IResult? denied)
    {
        if (TryResolveDepositor(httpContext, appId, out var writer, out denied) && writer is not null)
        {
            tenantId = writer.TenantId;
            authorSid = writer.AuthorSid;
            return true;
        }

        tenantId = null;
        authorSid = null;
        return false;
    }

    public static DocAccessScope ResolveReadScope(HttpContext httpContext)
    {
        try
        {
            string? tenant = null;
            try
            {
                tenant = EndpointSecurity.GetRequestTenant(httpContext).Value;
            }
            catch (Exception ex) when (ex is SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
            {
                return DocAccessScope.OfficialOnly;
            }

            var caller = new DocCaller(
                IsCanonicalClusterAdmin: EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User),
                IsGovernanceAdmin: GatewayPolicies.HasRole(httpContext.User, GatewayRole.GovernanceAdmin),
                TenantId: tenant,
                AuthorSid: EndpointSecurity.GetCallerIdentity(httpContext),
                HasSession: httpContext.User.Identity?.IsAuthenticated ?? false,
                IsReadOnlySession: false);

            return DocAccessPolicy.ResolveReader(caller);
        }
        catch (Exception ex) when (ex is SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            return DocAccessScope.OfficialOnly;
        }
    }

    public static bool IsGlobalApplication(string? appId) =>
        DocumentRules.IsReservedAppId(appId);

    public static IResult Forbidden(string message) =>
        ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, message);
}

public sealed record DepositDocumentBody(
    string? Title,
    string Content,
    string? ErrorCode = null,
    string[]? Tags = null);
