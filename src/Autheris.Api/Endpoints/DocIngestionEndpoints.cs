namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
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
            // Cluster administrators publish official documentation; every other caller's bundle belongs to the caller's tenant
            // (served to that tenant only, marked as untrusted user content) and can never replace an existing document.
            if (!DocEndpointAccess.TryResolveDepositor(request.HttpContext, appId, out var depositTenantId, out var depositAuthorSid, out var denied))
            {
                return denied!;
            }

            if (!request.HasFormContentType)
            {
                // Raw stream upload
                const long maxRawSize = 50 * 1024 * 1024;
                if (request.ContentLength > maxRawSize)
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Bundle archive exceeds maximum permitted size of 50 MB.", request.HttpContext);
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
                        return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Bundle archive exceeds maximum permitted size of 50 MB.", request.HttpContext);
                    }
                    await ms.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                }

                if (ms.Length == 0)
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "No documentation bundle file found in the request.", request.HttpContext);
                }

                ms.Position = 0;
                var result = DocEndpointAccess.Scrub(await docStore.ExtractBundleAsync(appId, ms, depositTenantId, depositAuthorSid, ct).ConfigureAwait(false));
                return BundleResult(result);
            }

            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("bundle") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file == null || file.Length == 0)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "No documentation bundle file found in the request.", request.HttpContext);
            }

            // Guard against oversized archives (e.g. 50 MB limit)
            if (file.Length > 50 * 1024 * 1024)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Bundle archive exceeds maximum permitted size of 50 MB.", request.HttpContext);
            }

            await using var stream = file.OpenReadStream();
            var extractResult = DocEndpointAccess.Scrub(await docStore.ExtractBundleAsync(appId, stream, depositTenantId, depositAuthorSid, ct).ConfigureAwait(false));
            return BundleResult(extractResult);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionBundleApplied);

        // 2. Deposit or update individual document
        app.MapPut("/api/v1/docs/apps/{appId}/documents/{category}/{docId}", async (
            string appId,
            string category,
            string docId,
            DepositDocumentBody body,
            HttpContext httpContext,
            IUniversalDocStore docStore,
            IGatewayEventBus? eventBus,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<DocCategory>(category, true, out var parsedCategory))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The document category is invalid.", httpContext);
            }

            if (string.IsNullOrWhiteSpace(body.Content))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Document content cannot be empty.", httpContext);
            }

            // Cluster administrators deposit official documentation; every other caller's document belongs to the caller's tenant
            // (served to that tenant only, marked as untrusted user content), exactly like the MCP deposit tool.
            if (!DocEndpointAccess.TryResolveDepositor(httpContext, appId, out var depositTenantId, out var depositAuthorSid, out var denied))
            {
                return denied!;
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
                depositTenantId,
                depositAuthorSid);

            // A tenant administrator may add documents but never replace an existing one (official or of another tenant).
            if (depositTenantId is not null && await docStore.DocumentExistsAsync(depositRequest, ct).ConfigureAwait(false))
            {
                return DocEndpointAccess.Forbidden("Overwriting an existing document requires ClusterAdmin role.");
            }

            var result = DocEndpointAccess.Scrub(await docStore.DepositDocumentAsync(depositRequest, ct).ConfigureAwait(false));

            if (result.Success && eventBus != null)
            {
                await eventBus.PublishAsync(new DocumentDepositedEvent(
                    EventId: Guid.NewGuid().ToString("N"),
                    AppId: appId,
                    Category: category,
                    DocId: docId,
                    Title: title,
                    RelativePath: DocumentDepositedEvent.BuildRelativePath(appId, category, docId),
                    Timestamp: DateTimeOffset.UtcNow,
                    TenantId: EndpointSecurity.GetRequestTenant(httpContext).Value), ct).ConfigureAwait(false);
            }

            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.DocIngestionDocumentApplied);

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
                : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The application manifest was not found.");
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 5. Search documentation REST API
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

        // 6. List documentation topics REST API
        app.MapGet("/api/v1/docs/topics", async (
            string? appId,
            HttpContext httpContext,
            IDocSearchService searchService,
            CancellationToken ct) =>
        {
            var topics = await searchService.ListTopicsAsync(DocEndpointAccess.ResolveReadScope(httpContext), appId, ct).ConfigureAwait(false);
            return Results.Ok(new { total = topics.Count, topics });
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.DocDiscoveryRead);

        // 7. Lookup error code REST API
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

        // 8. Retrieve procedure instructions REST API
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

    private static IResult BundleResult(DepositDocResult result)
    {
        if (result.Success)
        {
            return Results.Ok(result);
        }

        // A tenant bundle that would replace an existing document is a permission problem, not a malformed request.
        return string.Equals(result.ErrorCode, "DOC_OVERWRITE_FORBIDDEN", StringComparison.Ordinal)
            ? Results.Json(result, statusCode: StatusCodes.Status403Forbidden)
            : Results.BadRequest(result);
    }
}

/// <summary>
/// Tenant scoping of the REST documentation endpoints, mirroring the MCP documentation tools: cluster administrators deposit
/// official documentation, every other depositor deposits tenant documents, and readers see official documentation plus their
/// own tenant's documents.
/// </summary>
internal static class DocEndpointAccess
{
    /// <summary>
    /// Resolves who a deposit belongs to. A canonical ClusterAdmin deposits official (tenant-less) documentation
    /// (<paramref name="tenantId"/> is null). Every other caller deposits for the request tenant; without a resolvable tenant
    /// the request is refused (<paramref name="denied"/>), because the document could not be scoped and would read as official.
    /// Non-ClusterAdmin callers may not deposit for the global applications.
    /// </summary>
    public static bool TryResolveDepositor(HttpContext httpContext, string appId, out string? tenantId, out string? authorSid, out IResult? denied)
    {
        tenantId = null;
        authorSid = null;
        denied = null;

        try
        {
            authorSid = EndpointSecurity.GetCallerIdentity(httpContext);
            if (EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User))
            {
                return true;
            }

            var tenant = EndpointSecurity.GetRequestTenant(httpContext).Value;
            if (string.IsNullOrWhiteSpace(tenant))
            {
                denied = Forbidden("Depositing documentation requires a tenant-bound session.");
                return false;
            }

            if (IsGlobalApplication(appId))
            {
                denied = Forbidden("Depositing documentation for global application requires ClusterAdmin role.");
                return false;
            }

            tenantId = tenant.Trim();
            return true;
        }
        catch (SecurityException)
        {
            // A malformed tenant claim or a rejected cross-tenant request fails closed.
            authorSid = null;
            denied = Forbidden("Depositing documentation requires a tenant-bound session.");
            return false;
        }
    }

    /// <summary>Official documentation plus the caller's tenant documents; official documentation only without a resolvable tenant.</summary>
    public static DocAccessScope ResolveReadScope(HttpContext httpContext)
    {
        try
        {
            return DocAccessScope.ForTenant(EndpointSecurity.GetRequestTenant(httpContext).Value);
        }
        catch (SecurityException)
        {
            return DocAccessScope.OfficialOnly;
        }
    }

    public static bool IsGlobalApplication(string? appId) =>
        string.Equals(appId, "autheris", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(appId, "global", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(appId, "system", StringComparison.OrdinalIgnoreCase);

    /// <summary>Never discloses the server's absolute file system path: the result carries a store-relative path only.</summary>
    public static DepositDocResult Scrub(DepositDocResult result) =>
        result.FilePath is null ? result : result with { FilePath = DocProvenance.ToSafeSourcePath(result.FilePath) };

    public static IResult Forbidden(string message) =>
        ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, message);
}

public sealed record DepositDocumentBody(
    string? Title,
    string Content,
    string? ErrorCode = null,
    string[]? Tags = null);
