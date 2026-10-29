namespace Autheris.Api.Middleware;

using Autheris.Api.Errors;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Finding 3.3 / R13: read-only tokens (<see cref="TokenAccessScope"/>) may only send safe requests or POST to the
/// query endpoints. Those endpoints reject writes themselves (GraphQL mutations, WebSQL DML).
/// </summary>
public sealed class ReadOnlyTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PathString[] _queryPaths;

    public ReadOnlyTokenMiddleware(RequestDelegate next, PathString graphQlPath, PathString mcpPath)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _queryPaths =
        [
            graphQlPath,
            mcpPath,
            "/api/v1/sql",
            "/api/sql",
            "/api/v1/queries",
            "/api/v1/export/arrow"
        ];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.User.IsReadOnly() || IsPermitted(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        await ApiProblems.WriteAsync(context, StatusCodes.Status403Forbidden, ApiErrorCodes.ReadOnlyToken, "This token only permits read access.", context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsPermitted(HttpRequest request)
    {
        // SG-17: When ext_authz or reverse proxies probe with GET/HEAD/OPTIONS, check original method headers
        if (request.Headers.TryGetValue("X-Original-Method", out var origMethod) && !string.IsNullOrWhiteSpace(origMethod))
        {
            var om = origMethod.ToString().Trim().ToUpperInvariant();
            if (om is not "GET" and not "HEAD" and not "OPTIONS")
            {
                return false;
            }
        }
        if (request.Headers.TryGetValue("X-Forwarded-Method", out var fwdMethod) && !string.IsNullOrWhiteSpace(fwdMethod))
        {
            var fm = fwdMethod.ToString().Trim().ToUpperInvariant();
            if (fm is not "GET" and not "HEAD" and not "OPTIONS")
            {
                return false;
            }
        }

        if (HttpMethods.IsGet(request.Method) ||
            HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            return false;
        }

        // SR15-20: Root /api/v1/queries registration is forbidden for read-only tokens;
        // only query executions on sub-paths like /api/v1/queries/{name} are allowed.
        if (request.Path.Equals("/api/v1/queries", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/api/v1/queries/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // SEC-GOV175-01: Pre-flight query analysis is a zero-side-effect read operation permitted for read-only tokens
        if (request.Path.Equals(GovernanceQueryAnalyzePath, StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals(GovernanceQueryAnalyzePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsProcedureExecutionPath(request.Path.Value))
        {
            return true;
        }

        // SEC-DATA178-01: Bulk table keyset streaming is a governed single-table read permitted for read-only tokens
        if (IsDataBulkStreamPath(request.Path.Value))
        {
            return true;
        }

        return _queryPaths.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
    }

    private const string GovernanceQueryAnalyzePath = "/api/v1/governance/queries/analyze";
    private const string ProceduresPrefix = "/api/v1/procedures";

    /// <summary>
    /// D-6 / SEC-PROC152I2-09: POST to exactly <c>/api/v1/procedures/{one non-empty segment}</c>. Works on the raw path value
    /// (no decoding, no trimming); only the prefix and <c>openapi.json</c> compare case-insensitively. Governed procedures
    /// are read-only by contract (the service refuses write-mode definitions), so executing one is a read.
    /// </summary>
    private static bool IsProcedureExecutionPath(string? path)
    {
        if (path is null || path.Length <= ProceduresPrefix.Length + 1 ||
            !path.StartsWith(ProceduresPrefix, StringComparison.OrdinalIgnoreCase) ||
            path[ProceduresPrefix.Length] != '/')
        {
            return false;
        }

        var segment = path.AsSpan(ProceduresPrefix.Length + 1);
        if (segment.IndexOfAny('/', ';') >= 0 ||
            segment.Contains("%2F", StringComparison.OrdinalIgnoreCase) ||
            segment.SequenceEqual(".") || segment.SequenceEqual("..") ||
            segment.Equals("openapi.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private const string DataPrefix = "/api/v1/data";

    /// <summary>
    /// SEC-DATA178-01: Permits POST /api/v1/data/{domain}/{schema}/{table}/(stream|bulk) for read-only tokens.
    /// Strict segment count, fail-closed against traversal, encoded characters, and mutation endpoints.
    /// </summary>
    internal static bool IsDataBulkStreamPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Must reject directory traversal sequences, non-normalized separators, encoded delimiters, semicolons, query parameters
        if (path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains(';', StringComparison.Ordinal) ||
            path.Contains("%2F", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("%5C", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            path.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        if (!path.StartsWith(DataPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Expected segments: ["api", "v1", "data", "{domain}", "{schema}", "{table}", "stream" or "bulk"]
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 7)
        {
            return false;
        }

        if (!string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], "data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var domain = segments[3];
        var schema = segments[4];
        var table = segments[5];
        var action = segments[6];

        if (string.IsNullOrWhiteSpace(domain) || domain == "." || domain == ".." ||
            string.IsNullOrWhiteSpace(schema) || schema == "." || schema == ".." ||
            string.IsNullOrWhiteSpace(table) || table == "." || table == "..")
        {
            return false;
        }

        if (!string.Equals(action, "stream", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "bulk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
