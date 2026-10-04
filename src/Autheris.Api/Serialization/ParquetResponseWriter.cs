namespace Autheris.Api.Serialization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

/// <summary>
/// Endpoint metadata marker: the route produces Parquet itself when the client negotiates it via the Accept header.
/// Routes without this marker (and other than GraphQL) answer 406 to a Parquet-only Accept header.
/// </summary>
public sealed class ParquetOutputSupportedMetadata
{
}

/// <summary>
/// F-DATA-01: Writes already governed rows as an Apache Parquet response. Must only be called after the complete
/// governance pipeline (RLS, masking, consent) produced the rows that the JSON path would return as well.
/// </summary>
public static class ParquetResponseWriter
{
    private const int MaxFileNameLength = 120;
    private const string ExportDomain = "export";
    private const string ExportSchema = "public";
    private const string DefaultTableHint = "result";

    /// <summary>
    /// Returns the Parquet egress options of the request scope (defaults when not registered).
    /// </summary>
    internal static ParquetEgressOptions GetEgressOptions(HttpContext context)
    {
        var options = context.RequestServices?.GetService<IOptions<GatewayOptions>>();
        return options?.Value.ParquetEgress ?? new ParquetEgressOptions();
    }

    /// <summary>
    /// Writes 406 when Parquet output is disabled or the export service is unavailable. Channels call this
    /// BEFORE executing the query so no data is read for a response that cannot be produced.
    /// </summary>
    /// <returns><c>true</c> when the request was rejected and the response is complete.</returns>
    public static async Task<bool> TryRejectUnavailableAsync(HttpContext context, IParquetExportService? service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (service != null && GetEgressOptions(context).Enabled)
        {
            return false;
        }

        await WriteNotAcceptableAsync(
            context,
            "Parquet output (Accept: " + ParquetContentNegotiation.ParquetMediaType + ") is disabled in the gateway configuration.",
            ["application/json"],
            ct).ConfigureAwait(false);
        return true;
    }

    public static async Task WriteAsync(
        HttpContext ctx,
        IParquetExportService svc,
        string tableHint,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string>? columns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(rows);

        if (await TryRejectUnavailableAsync(ctx, svc, ct).ConfigureAwait(false))
        {
            return;
        }

        var egress = GetEgressOptions(ctx);
        var safeTable = SanitizeName(tableHint);
        var request = new ParquetExportRequest(
            Table: new TableIdentifier(ExportDomain, ExportSchema, safeTable),
            Columns: columns ?? Array.Empty<string>(),
            Limit: egress.MaxRowsPerFile,
            FlattenNested: egress.FlattenNestedStructures);

        var result = await svc.ExportToParquetAsync(request, rows, ct).ConfigureAwait(false);

        var fileName = result.SuggestedFileName.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)
            ? result.SuggestedFileName[..^".parquet".Length]
            : result.SuggestedFileName;
        var contentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = "\"" + SanitizeName(fileName) + ".parquet\""
        };

        var response = ctx.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ParquetContentNegotiation.ParquetMediaType;
        response.ContentLength = result.Data.Length;
        response.Headers.ContentDisposition = contentDisposition.ToString();
        response.Headers["X-Row-Count"] = result.RowCount.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Export-Truncated"] = result.IsTruncated ? "true" : "false";
        response.Headers.Vary = "Accept";
        response.Headers.CacheControl = "no-store";

        await response.Body.WriteAsync(result.Data, ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task WriteNotAcceptableAsync(HttpContext context, string error, IReadOnlyList<string> supported, CancellationToken ct)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status406NotAcceptable;
        response.ContentLength = null;
        response.Headers.Vary = "Accept";
        await response.WriteAsJsonAsync(new { error, supported }, ct).ConfigureAwait(false);
    }

    /// <summary>Reduces a table hint / file name to [A-Za-z0-9_] (no path, quote or CRLF characters).</summary>
    internal static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultTableHint;
        }

        var sb = new StringBuilder(Math.Min(value.Length, MaxFileNameLength));
        foreach (var c in value)
        {
            if (sb.Length >= MaxFileNameLength)
            {
                break;
            }

            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        var sanitized = sb.ToString().Trim('_');
        return sanitized.Length == 0 ? DefaultTableHint : sanitized;
    }
}
