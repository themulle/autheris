namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;

internal static class CatalogDiscoveryApiHelpers
{
    public static async Task<IResult> ListDatasetsAsync(
        ICatalogDiscoveryService discoveryService,
        HttpContext httpContext,
        Func<HttpContext, string?> applyLanguage,
        Func<HttpContext, string?, RequestContext> createContext,
        CancellationToken ct)
    {
        try
        {
            var targetLang = applyLanguage(httpContext);
            var context = createContext(httpContext, targetLang);
            var datasets = await discoveryService.ListDatasetsAsync(context, ct).ConfigureAwait(false);
            return Results.Ok(datasets);
        }
        catch (DiscoveryStatusException ex)
        {
            return MapException(ex, httpContext);
        }
    }

    public static async Task<IResult> GetDatasetDetailAsync(
        string datasetId,
        ICatalogDiscoveryService discoveryService,
        HttpContext httpContext,
        Func<HttpContext, string?> applyLanguage,
        Func<HttpContext, string?, RequestContext> createContext,
        CancellationToken ct)
    {
        if (!TableIdentifier.TryParse(datasetId.Replace('/', '.'), out var tableId))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The dataset identifier is invalid.", httpContext);
        }

        try
        {
            var targetLang = applyLanguage(httpContext);
            var context = createContext(httpContext, targetLang);
            var detail = await discoveryService.GetDatasetDetailAsync(tableId, context, ct).ConfigureAwait(false);
            if (detail is null)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "Requested dataset was not found.", httpContext);
            }

            var isBrief = httpContext.Request.Query.TryGetValue("brief", out var briefVal) &&
                          bool.TryParse(briefVal.ToString(), out var b) && b;
            if (isBrief)
            {
                var briefDetail = new
                {
                    dataset = detail.DatasetId,
                    domain = detail.Domain,
                    schema = detail.Schema,
                    table = detail.Table,
                    description = detail.Description,
                    sensitivity = detail.Sensitivity,
                    primaryKeys = detail.PrimaryKeys,
                    size = detail.Size,
                    columns = detail.Columns.Select(c => new
                    {
                        name = c.Name,
                        type = c.Type,
                        description = c.Description,
                        isPrimaryKey = c.IsPrimaryKey,
                        isSensitive = c.Sensitivity != "Public" && c.Sensitivity != "NORMAL" && !string.IsNullOrEmpty(c.Sensitivity)
                    })
                };
                return Results.Ok(briefDetail);
            }

            return Results.Ok(detail);
        }
        catch (DiscoveryStatusException ex)
        {
            return MapException(ex, httpContext);
        }
    }

    public static async Task<IResult> ListDomainsAsync(
        ICatalogDiscoveryService discoveryService,
        HttpContext httpContext,
        Func<HttpContext, string?, RequestContext> createContext,
        CancellationToken ct)
    {
        try
        {
            var context = createContext(httpContext, null);
            var datasets = await discoveryService.ListDatasetsAsync(context, ct).ConfigureAwait(false);
            var domains = datasets
                .GroupBy(d => d.Domain, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { domain = g.Key, tableCount = g.Count() })
                .OrderByDescending(d => d.tableCount)
                .ToList();
            return Results.Ok(new { totalTables = datasets.Count, totalDomains = domains.Count, domains });
        }
        catch (DiscoveryStatusException ex)
        {
            return MapException(ex, httpContext);
        }
    }

    public static async Task<IResult> SearchAsync(
        string? q,
        string? domain,
        int? limit,
        string? mode,
        ICatalogDiscoveryService discoveryService,
        HttpContext httpContext,
        Func<HttpContext, string?> applyLanguage,
        Func<HttpContext, string?, RequestContext> createContext,
        CancellationToken ct)
    {
        try
        {
            var targetLang = applyLanguage(httpContext);
            var context = createContext(httpContext, targetLang);
            var searchMode = mode?.ToLowerInvariant() switch
            {
                "semantic" => CatalogSearchMode.SemanticVector,
                "keyword" => CatalogSearchMode.KeywordBm25,
                _ => CatalogSearchMode.Hybrid
            };

            var query = new CatalogSearchQuery(
                QueryText: q ?? string.Empty,
                DomainFilter: domain,
                Limit: Math.Clamp(limit ?? 10, 1, 50),
                Mode: searchMode,
                Language: targetLang);

            var hits = await discoveryService.SearchCatalogDetailedAsync(query, context, ct).ConfigureAwait(false);
            return Results.Ok(hits);
        }
        catch (DiscoveryStatusException ex)
        {
            return MapException(ex, httpContext);
        }
    }

    internal static IResult MapException(DiscoveryStatusException ex, HttpContext httpContext)
    {
        var logger = (httpContext.RequestServices?.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as Microsoft.Extensions.Logging.ILoggerFactory)
            ?.CreateLogger("Autheris.Api.Endpoints.CatalogDiscoveryApiHelpers");
        if (logger is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Catalog discovery request failed ({Kind}).", ex.Kind);

        return ex.Kind switch
        {
            DiscoveryFailureKind.Unauthenticated => ApiProblems.Unauthorized(
                ApiErrorCodes.Unauthorized,
                "Authentication is required for catalog discovery.",
                httpContext),
            DiscoveryFailureKind.Forbidden => ApiProblems.Forbidden(
                ApiErrorCodes.DiscoveryNotPermitted,
                "Catalog discovery is not permitted for the current caller.",
                httpContext),
            DiscoveryFailureKind.RateLimited => ApiProblems.TooManyRequests(
                ApiErrorCodes.RateLimitExceeded,
                "Catalog discovery rate limit exceeded. Retry later.",
                httpContext,
                ex.RetryAfterSeconds ?? 1),
            _ => ApiProblems.BadRequest(
                ApiErrorCodes.InvalidQuery,
                "The catalog discovery request is invalid.",
                httpContext)
        };
    }
}
