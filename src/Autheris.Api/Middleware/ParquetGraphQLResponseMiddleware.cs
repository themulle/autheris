namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Serialization;
using Autheris.Application.Serialization;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-DATA-01: Parquet output for GraphQL and central 406 handling for routes that cannot produce Parquet.
/// Registered directly BEFORE <see cref="GatewayExtensibilityMiddleware"/>, so ingress/egress interceptors and audit
/// still operate on the governed JSON response; the Parquet conversion is the outermost, pure output transformation.
/// </summary>
public sealed class ParquetGraphQLResponseMiddleware
{
    internal const string ConversionHeader = "X-Parquet-Conversion";
    private const string DownstreamAcceptHeader = "application/graphql-response+json, application/json";
    private const long DefaultMaxBufferedSourceBytes = 64 * 1024 * 1024;

    private static readonly string[] SupportedOnGraphQL = ["application/graphql-response+json", "application/json", ParquetContentNegotiation.ParquetMediaType];
    private static readonly string[] SupportedOnOtherRoutes = ["application/json"];
    private static readonly string[] RowArrayPropertyNames = ["rows", "items", "nodes"];

    private readonly RequestDelegate _next;
    private readonly GatewayOptions _options;
    private readonly IParquetExportService _parquetService;
    private readonly ILogger<ParquetGraphQLResponseMiddleware> _logger;
    private readonly string _graphQlPath;
    private readonly string _mcpBasePath;

    public ParquetGraphQLResponseMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IParquetExportService parquetService,
        ILogger<ParquetGraphQLResponseMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _parquetService = parquetService ?? throw new ArgumentNullException(nameof(parquetService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _graphQlPath = _options.GraphQL.EndpointPath.StartsWith('/')
            ? _options.GraphQL.EndpointPath
            : "/" + _options.GraphQL.EndpointPath;
        _mcpBasePath = GatewayApplicationBuilderExtensions.ResolveMcpBasePath(_options);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var accept = ParquetContentNegotiation.Evaluate(context.Request);
        if (!accept.ParquetPreferred || IsExcludedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments(_graphQlPath))
        {
            // Subscriptions (WebSocket / SSE / multipart) are never converted.
            if (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsPost(context.Request.Method)) ||
                GatewayExtensibilityMiddleware.IsStreamingRequest(context))
            {
                await _next(context);
                return;
            }

            if (!_options.ParquetEgress.Enabled)
            {
                if (accept.HasJsonAlternative)
                {
                    await _next(context);
                    return;
                }

                await ParquetResponseWriter.WriteNotAcceptableAsync(
                    context,
                    "Parquet output is disabled in the gateway configuration.",
                    SupportedOnOtherRoutes,
                    context.RequestAborted);
                return;
            }

            await HandleGraphQLAsync(context);
            return;
        }

        // Routes that render Parquet themselves (WebSQL, SQL endpoints, OData entity sets, export)
        if (context.GetEndpoint()?.Metadata.GetMetadata<ParquetOutputSupportedMetadata>() != null)
        {
            await _next(context);
            return;
        }

        if (accept.HasJsonAlternative)
        {
            await _next(context);
            return;
        }

        await ParquetResponseWriter.WriteNotAcceptableAsync(
            context,
            "This route cannot produce Apache Parquet. Parquet output is available on GraphQL, WebSQL, SQL endpoints and OData entity sets.",
            SupportedOnOtherRoutes,
            context.RequestAborted);
    }

    /// <summary>
    /// MCP (JSON-RPC envelope), webhooks, health probes and metrics are never converted or rejected.
    /// </summary>
    private bool IsExcludedPath(PathString path) =>
        path.StartsWithSegments(_mcpBasePath) ||
        path.StartsWithSegments("/api/webhooks") ||
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/metrics") ||
        (path.Value?.Contains("/webhook", StringComparison.OrdinalIgnoreCase) ?? false);

    private async Task HandleGraphQLAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var originalAccept = context.Request.Headers.Accept;
        var originalBody = context.Response.Body;
        var maxBytes = _options.ParquetEgress.MaxBufferedSourceBytes > 0
            ? _options.ParquetEgress.MaxBufferedSourceBytes
            : DefaultMaxBufferedSourceBytes;

        // The GraphQL server, egress interceptors and audit see a plain JSON request/response.
        context.Request.Headers.Accept = DownstreamAcceptHeader;

        // Hard limit: data beyond the limit is discarded (Stream.Null), never forwarded as JSON.
        await using var capture = new BoundedResponseBufferStream(Stream.Null, maxBytes, context.Response);
        context.Response.Body = capture;
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
            context.Request.Headers.Accept = originalAccept;
        }

        context.Response.Headers.Vary = "Accept";

        if (capture.IsPassThrough)
        {
            if (string.Equals(capture.PassThroughReason, "BufferLimitExceeded", StringComparison.Ordinal))
            {
                _logger.LogWarning("GraphQL response exceeded the Parquet source buffer limit of {Limit} bytes. TraceId={TraceId}", maxBytes, context.TraceIdentifier);
                await WriteJsonErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                    $"The GraphQL result exceeds the Parquet conversion limit of {maxBytes} bytes. Use paging or a smaller selection.", ct);
            }
            else
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status406NotAcceptable,
                    "Streaming GraphQL responses cannot be converted to Apache Parquet.", ct);
            }
            return;
        }

        var sourceBytes = capture.GetBufferedBytes();
        if (context.Response.StatusCode != StatusCodes.Status200OK || !IsJsonContentType(context.Response.ContentType))
        {
            await WriteOriginalAsync(context, sourceBytes, ct);
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(sourceBytes);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GraphQL response could not be parsed for Parquet conversion. TraceId={TraceId}", context.TraceIdentifier);
            await WriteOriginalAsync(context, sourceBytes, ct);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteOriginalAsync(context, sourceBytes, ct);
                return;
            }

            // GraphQL errors are never hidden inside a Parquet file: the governed JSON is returned unchanged.
            if (root.TryGetProperty("errors", out var errors) &&
                errors.ValueKind != JsonValueKind.Null &&
                !(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0))
            {
                context.Response.Headers[ConversionHeader] = "skipped-errors";
                await WriteOriginalAsync(context, sourceBytes, ct);
                return;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status406NotAcceptable,
                    "The GraphQL response contains no 'data' object that could be converted to Apache Parquet.", ct);
                return;
            }

            string? rootFieldName = null;
            JsonElement rootField = default;
            var rootFieldCount = 0;
            foreach (var property in data.EnumerateObject())
            {
                if (string.Equals(property.Name, "__typename", StringComparison.Ordinal))
                {
                    continue;
                }

                rootFieldCount++;
                rootFieldName = property.Name;
                rootField = property.Value;
            }

            if (rootFieldCount != 1 || rootFieldName is null)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status406NotAcceptable,
                    "Parquet output requires exactly one root field in the GraphQL operation (found " + rootFieldCount + ").", ct);
                return;
            }

            if (!TryExtractRows(rootField, out var rows, out var extractionError))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status406NotAcceptable,
                    $"The root field '{ParquetResponseWriter.SanitizeName(rootFieldName)}' cannot be converted to Apache Parquet: {extractionError}", ct);
                return;
            }

            try
            {
                // Headers must be set before the body is written (response start).
                context.Response.Headers[ConversionHeader] = "converted";
                await ParquetResponseWriter.WriteAsync(context, _parquetService, rootFieldName, rows, null, ct);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "GraphQL result could not be serialized as Parquet. TraceId={TraceId}", context.TraceIdentifier);
                if (!context.Response.HasStarted)
                {
                    context.Response.Headers[ConversionHeader] = "failed";
                    await WriteJsonErrorAsync(context, StatusCodes.Status406NotAcceptable,
                        "The GraphQL result cannot be represented as Apache Parquet (invalid column names).", ct);
                }
            }
        }
    }

    /// <summary>
    /// Row sources: <c>jsonRows</c> (JSON string array or list of JSON strings), <c>rows</c>/<c>items</c>/<c>nodes</c>,
    /// <c>edges[].node</c>, or the root field itself being a list of objects.
    /// </summary>
    internal static bool TryExtractRows(JsonElement field, out List<IReadOnlyDictionary<string, object?>> rows, out string error)
    {
        rows = [];
        error = string.Empty;

        switch (field.ValueKind)
        {
            case JsonValueKind.Array:
                return TryReadObjectArray(field, rows, out error);

            case JsonValueKind.Object:
                if (field.TryGetProperty("jsonRows", out var jsonRows))
                {
                    return TryReadJsonRows(jsonRows, rows, out error);
                }

                foreach (var name in RowArrayPropertyNames)
                {
                    if (field.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        return TryReadObjectArray(list, rows, out error);
                    }
                }

                if (field.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var edge in edges.EnumerateArray())
                    {
                        if (edge.ValueKind != JsonValueKind.Object ||
                            !edge.TryGetProperty("node", out var node) ||
                            node.ValueKind != JsonValueKind.Object)
                        {
                            error = "every edge must contain a 'node' object.";
                            return false;
                        }

                        rows.Add(ToRow(node));
                    }
                    return true;
                }

                error = "the result object contains no row list (jsonRows, rows, items, nodes or edges).";
                return false;

            default:
                error = "the result is a scalar or null value, Parquet requires a list of objects.";
                return false;
        }
    }

    private static bool TryReadObjectArray(JsonElement array, List<IReadOnlyDictionary<string, object?>> rows, out string error)
    {
        error = string.Empty;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "the list contains scalar values, Parquet requires a list of objects.";
                return false;
            }

            rows.Add(ToRow(item));
        }

        return true;
    }

    private static bool TryReadJsonRows(JsonElement jsonRows, List<IReadOnlyDictionary<string, object?>> rows, out string error)
    {
        error = string.Empty;
        try
        {
            switch (jsonRows.ValueKind)
            {
                case JsonValueKind.String:
                {
                    using var parsed = JsonDocument.Parse(jsonRows.GetString() ?? "[]");
                    if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        error = "'jsonRows' must contain a JSON array.";
                        return false;
                    }

                    foreach (var item in parsed.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            error = "'jsonRows' must contain JSON objects.";
                            return false;
                        }

                        rows.Add(ToRow(item.Clone()));
                    }
                    return true;
                }

                case JsonValueKind.Array:
                    foreach (var item in jsonRows.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            rows.Add(ToRow(item));
                            continue;
                        }

                        if (item.ValueKind != JsonValueKind.String)
                        {
                            error = "'jsonRows' entries must be JSON objects or JSON strings.";
                            return false;
                        }

                        using var parsedRow = JsonDocument.Parse(item.GetString() ?? "null");
                        if (parsedRow.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            error = "'jsonRows' entries must contain JSON objects.";
                            return false;
                        }

                        rows.Add(ToRow(parsedRow.RootElement.Clone()));
                    }
                    return true;

                default:
                    error = "'jsonRows' must be a JSON string or a list.";
                    return false;
            }
        }
        catch (JsonException)
        {
            error = "'jsonRows' contains invalid JSON.";
            return false;
        }
    }

    private static Dictionary<string, object?> ToRow(JsonElement element)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            row[property.Name] = property.Value;
        }

        return row;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteOriginalAsync(HttpContext context, byte[] body, CancellationToken ct)
    {
        context.Response.ContentLength = body.Length;
        if (body.Length > 0)
        {
            await context.Response.Body.WriteAsync(body, ct);
        }
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string error, CancellationToken ct)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = null;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error, supported = SupportedOnGraphQL }, ct);
    }
}
