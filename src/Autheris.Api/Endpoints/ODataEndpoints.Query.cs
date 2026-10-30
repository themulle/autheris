namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Serialization;
using Autheris.Application.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Extensions.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static partial class ODataEndpoints
{
    /// <summary>
    /// Befund 4a.2 & SR15-31: Support single-segment entity set name path (/odata/v4/lwetem_prod_md_crane) as an alias
    /// matching the OData 4.0 CSDL EntitySet name, strictly isolated to the caller's tenant catalog.
    /// </summary>
    internal static async Task<IResult> HandleFlatEntitySetRequestAsync(
        string entitySetName,
        IODataHandler odataHandler,
        ITableMetadataRepository metadataRepo,
        HttpContext context)
    {
        if (entitySetName.StartsWith('$'))
        {
            return Results.NotFound();
        }

        var tables = await metadataRepo.GetAllTablesAsync(context.RequestAborted).ConfigureAwait(false);
        // DT-A-7: the default tenant (catalog owner) may address every table by its flat name; every other tenant only its own domain and the
        // shared "default" domain. An unresolved tenant addresses nothing.
        var tenantResolver = context.RequestServices?.GetService<Autheris.Application.Tenancy.ITenantResolver>();
        var resolution = tenantResolver?.Resolve(context.User)
            ?? Autheris.Application.Catalog.Services.CallerScopeResolver.ResolutionOf(context.User, null);
        var candidateTables = new List<Autheris.Domain.Model.TableMetadata>();
        if (resolution.Outcome is Autheris.Domain.Tenancy.TenantResolutionOutcome.FromClaim
                or Autheris.Domain.Tenancy.TenantResolutionOutcome.FromRealm
                or Autheris.Domain.Tenancy.TenantResolutionOutcome.DefaultAssigned &&
            resolution.Tenant is { } tenant && tenant.Value is not null)
        {
            var isOwner = tenantResolver?.IsDefaultTenant(tenant) == true;
            candidateTables = Autheris.Application.Services.CatalogVisibility.DropForeignVirtualTables(tables, tenant)
                .Where(t => isOwner ||
                            string.Equals(t.Identifier.Domain, tenant.Value, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Identifier.Domain, "default", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var matchingTables = candidateTables
            .Where(t => string.Equals(ODataCsdlGenerator.GetEntityName(t), entitySetName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingTables.Count != 1)
        {
            var env = context.RequestServices?.GetService<IHostEnvironment>();
            if (env != null && !env.IsDevelopment())
            {
                return Results.Json(
                    new { error = new { code = "ACCESS_DENIED", message = "Access to the table is denied or the table does not exist." } },
                    statusCode: StatusCodes.Status403Forbidden,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }

            return Results.NotFound(new { error = new { code = "ResourceNotFound", message = "The requested entity set was not found." } });
        }

        var table = matchingTables[0];
        return await HandleEntitySetRequestAsync(
            table.Identifier.Domain,
            table.Identifier.Schema,
            table.Identifier.TableName,
            odataHandler,
            context).ConfigureAwait(false);
    }

    internal static async Task<IResult> HandleEntitySetRequestAsync(
        string domain,
        string schema,
        string tableName,
        IODataHandler odataHandler,
        HttpContext context)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        var serviceRoot = ResolveServiceRoot(context);
        var tableId = new TableIdentifier(domain, schema, tableName);

        var optionError = ValidateSystemQueryOptions(context.Request.Query);
        if (optionError != null)
        {
            return optionError;
        }

        int? top = null;
        if (context.Request.Query.TryGetValue("$top", out var topVal))
        {
            if (!int.TryParse(topVal, out var t) || t < 0)
            {
                return Results.Json(
                    new { error = new { code = "InvalidQueryOption", message = "The query parameter '$top' must be a non-negative integer." } },
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }
            top = t;
        }

        int? skip = null;
        if (context.Request.Query.TryGetValue("$skip", out var skipVal))
        {
            if (!long.TryParse(skipVal, out var s) || s < 0)
            {
                return Results.Json(
                    new { error = new { code = "InvalidQueryOption", message = "The query parameter '$skip' must be a non-negative integer." } },
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }
            if (s > ODataHandler.MaxSkip)
            {
                return Results.Json(
                    new { error = new { code = "InvalidQueryOption", message = $"The query parameter '$skip' must not exceed {ODataHandler.MaxSkip}." } },
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }
            skip = (int)s;
        }

        string? select = context.Request.Query["$select"].FirstOrDefault();
        string? orderBy = context.Request.Query.TryGetValue("$orderby", out var orderByVal) ? orderByVal.ToString() : null;
        string? filter = context.Request.Query.TryGetValue("$filter", out var filterVal) ? filterVal.ToString() : null;
        bool includeCount = false;
        if (context.Request.Query.TryGetValue("$count", out var countVal))
        {
            if (!bool.TryParse(countVal, out includeCount))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", "The query parameter '$count' must be 'true' or 'false'.");
            }
        }
        else if (context.Request.Query.TryGetValue("$inlinecount", out var inlineCountVal))
        {
            includeCount = string.Equals(inlineCountVal.ToString(), "allpages", StringComparison.OrdinalIgnoreCase);
        }

        // Befund 1.2: Strict Content Negotiation. If Accept header requests non-OData formats (CSV, NDJSON, etc.), reject with 406.
        if (context.Request.Headers.Accept.Count > 0)
        {
            var acceptEval = ParquetContentNegotiation.Evaluate(context.Request);
            if (!acceptEval.ParquetPreferred && !acceptEval.HasJsonAlternative)
            {
                return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            }
        }

        // F-DATA-01 / Befund 1.2: a Parquet request (via Accept header or $format=parquet) that cannot be served is rejected before the query is executed
        bool parquetRequested = ParquetContentNegotiation.IsParquetRequested(context.Request) ||
            string.Equals(context.Request.Query["$format"], "parquet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.Request.Query["$format"], "application/vnd.apache.parquet", StringComparison.OrdinalIgnoreCase);
        IParquetExportService? parquetService = null;
        if (parquetRequested)
        {
            parquetService = context.RequestServices?.GetService<IParquetExportService>();
            if (await ParquetResponseWriter.TryRejectUnavailableAsync(context, parquetService, context.RequestAborted))
            {
                return Results.Empty;
            }
        }

        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? string.Empty).ToArray());

        var result = await odataHandler.ExecuteEntitySetQueryAsync(
            principal: context.User,
            serviceRootUrl: serviceRoot,
            table: tableId,
            top: top,
            skip: skip,
            select: select,
            includeCount: includeCount,
            headers: headers,
            orderBy: orderBy,
            filter: filter,
            ct: context.RequestAborted
        );

        if (result.RetryAfterSeconds is int retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        if (parquetRequested && parquetService != null && result.StatusCode == StatusCodes.Status200OK)
        {
            var rows = ExtractEntitySetRows(result.Payload);
            await ParquetResponseWriter.WriteAsync(context, parquetService, tableName, rows, null, context.RequestAborted);
            return Results.Empty;
        }

        return Results.Json(result.Payload, statusCode: result.StatusCode, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
    }

    /// <summary>
    /// 4a.3: <c>/$count</c> answers the number of rows the caller may read (same row filter as the entity set) as plain
    /// text, as OData 4.0 specifies for the count segment.
    /// </summary>
    internal static async Task<IResult> HandleCountRequestAsync(
        string domain,
        string schema,
        string tableName,
        IODataHandler odataHandler,
        HttpContext context)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        foreach (var (key, _) in context.Request.Query)
        {
            if (key.StartsWith('$'))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", "The '/$count' segment takes no query options.");
            }
        }

        var serviceRoot = ResolveServiceRoot(context);
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? string.Empty).ToArray());
        var result = await odataHandler.ExecuteEntitySetQueryAsync(
            principal: context.User,
            serviceRootUrl: serviceRoot,
            table: new TableIdentifier(domain, schema, tableName),
            top: 1,
            skip: null,
            select: null,
            includeCount: true,
            headers: headers,
            ct: context.RequestAborted).ConfigureAwait(false);

        if (result.RetryAfterSeconds is int retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        if (result.StatusCode != StatusCodes.Status200OK ||
            result.Payload is not IReadOnlyDictionary<string, object?> payload ||
            !payload.TryGetValue("@odata.count", out var count) || count == null)
        {
            var status = result.StatusCode == StatusCodes.Status200OK ? StatusCodes.Status501NotImplemented : result.StatusCode;
            return Results.Json(result.Payload, statusCode: status, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }

        return Results.Text(Convert.ToString(count, CultureInfo.InvariantCulture), "text/plain; charset=utf-8");
    }

    /// <summary>System query options the entity set endpoint implements.</summary>
    private static readonly FrozenSet<string> SupportedSystemQueryOptions =
        new[] { "$top", "$skip", "$select", "$count", "$inlinecount", "$orderby", "$filter", "$format" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>OData v4 system query options that exist but are not implemented yet (answered with 501, never ignored).</summary>
    private static readonly FrozenSet<string> NotImplementedSystemQueryOptions =
        new[] { "$expand", "$search", "$apply", "$compute", "$skiptoken", "$deltatoken", "$levels", "$index", "$schemaversion", "$id" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O3/O4 (docs/plans/rls-subquery-in-strategy.md): a system query option is either applied or rejected. Ignoring
    /// $filter would hand the client unfiltered rows it believes to be filtered. Custom options (without '$') pass.
    /// </summary>
    internal static IResult? ValidateSystemQueryOptions(IQueryCollection query)
    {
        foreach (var (key, values) in query)
        {
            if (!key.StartsWith('$'))
            {
                continue;
            }

            var shownKey = key.Length > 64 ? key[..64] : key;
            if (NotImplementedSystemQueryOptions.Contains(key))
            {
                return ODataError(StatusCodes.Status501NotImplemented, "NotImplemented", $"The query option '{shownKey}' is not supported by this service.");
            }
            if (!SupportedSystemQueryOptions.Contains(key))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", $"The query option '{shownKey}' is unknown.");
            }
            if (values.Count > 1)
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", $"The query option '{shownKey}' must not be specified more than once.");
            }
            if (string.Equals(key, "$format", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(values.ToString(), "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(values.ToString(), "parquet", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(values.ToString(), "application/vnd.apache.parquet", StringComparison.OrdinalIgnoreCase) &&
                !values.ToString().StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", "The query option '$format' supports 'json' and 'parquet' only.");
            }
        }

        return null;
    }

    private static IResult ODataError(int statusCode, string code, string message) =>
        Results.Json(
            new { error = new { code, message } },
            statusCode: statusCode,
            contentType: "application/json;odata.metadata=minimal;charset=utf-8");

    /// <summary>
    /// Resolves the canonical OData service root URL based on whether the caller accessed /odata/v4 or /odata.
    /// Preserves request scheme, host, path base (if reverse proxied), and the appropriate base segment.
    /// </summary>
    internal static string ResolveServiceRoot(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isV4 = path.StartsWith("/odata/v4", StringComparison.OrdinalIgnoreCase);
        var baseSegment = isV4 ? "/odata/v4" : "/odata";
        var pathBase = context.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
        return $"{context.Request.Scheme}://{context.Request.Host}{pathBase}{baseSegment}";
    }

    /// <summary>
    /// Extracts the governed rows ("value") of an OData entity set payload without @odata annotations.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExtractEntitySetRows(object? payload)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (payload is null)
        {
            return rows;
        }

        if (payload is IReadOnlyDictionary<string, object?> dictionary &&
            dictionary.TryGetValue("value", out var value) &&
            value is IEnumerable<IReadOnlyDictionary<string, object?>> typedRows)
        {
            foreach (var row in typedRows)
            {
                rows.Add(StripODataAnnotations(row));
            }

            return rows;
        }

        // Fallback for other payload shapes: read "value" from the JSON representation the JSON path would send.
        var element = JsonSerializer.SerializeToElement(payload, payload.GetType());
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!IsODataAnnotation(property.Name))
                    {
                        row[property.Name] = property.Value;
                    }
                }
                rows.Add(row);
            }
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, object?> StripODataAnnotations(IReadOnlyDictionary<string, object?> row)
    {
        var hasAnnotation = false;
        foreach (var key in row.Keys)
        {
            if (IsODataAnnotation(key))
            {
                hasAnnotation = true;
                break;
            }
        }

        if (!hasAnnotation)
        {
            return row;
        }

        var stripped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, val) in row)
        {
            if (!IsODataAnnotation(key))
            {
                stripped[key] = val;
            }
        }

        return stripped;
    }

    private static bool IsODataAnnotation(string key) => key.Contains("@odata.", StringComparison.Ordinal);
}
