namespace Autheris.Api.Endpoints;

using System.Security.Cryptography;
using System.Text;
using Autheris.Api.Errors;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public static partial class WebSqlEndpoints
{
    internal const string IdempotencyKeyHeader = "Idempotency-Key";
    internal const int MaxIdempotencyKeyLength = 128;
    private static readonly TimeSpan IdempotencyReservationTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Classifies by the parsed statement type (the same analyzer the rewriter uses), never by text prefix.
    /// Text that does not parse is not DML here: the executor refuses it.
    /// </summary>
    internal static bool IsDmlStatement(string sql)
    {
        try
        {
            var normalized = SqlParameterExtractor.NormalizeForAst(sql.Trim());
            var (tree, _) = FastSqlEngine.Default.Parse(normalized.AsMemory());
            return new SqlQueryAnalyzer().Analyze(tree).StatementType
                is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Request-level guard for DML: refused on the Trino route, and on the WebSQL route admitted once per (tenant, caller, key).
    /// Returns <c>true</c> when the request was refused (the response is written).
    /// </summary>
    private static async Task<bool> RefuseDmlRequestAsync(
        HttpContext httpContext, string sql, bool isTrinoRoute, TenantId tenantId, ILogger logger, CancellationToken ct)
    {
        if (!IsDmlStatement(sql))
        {
            return false;
        }

        if (isTrinoRoute)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                "Data-changing statements are not supported on this route.", ct);
            return true;
        }

        var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "A canonical user identity is required.", ct);
            return true;
        }

        var key = httpContext.Request.Headers[IdempotencyKeyHeader].ToString();
        if (key.Length is 0 or > MaxIdempotencyKeyLength || !Ascii.IsValid(key) || key.Any(char.IsControl))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                $"Data-changing statements require an '{IdempotencyKeyHeader}' header of 1 to {MaxIdempotencyKeyLength} ASCII characters.", ct);
            return true;
        }

        var store = httpContext.RequestServices?.GetService<IIdempotencyStore>();
        if (store == null)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServiceUnavailable, "The request cannot be processed now; retry later.", ct);
            return true;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId.Value}\0{callerIdentity}\0{key}"));
        var reservationKey = "websql-dml:" + Convert.ToHexString(digest);
        bool reserved;
        try
        {
            reserved = await store.SetIfNotExistsAsync(reservationKey, "1", IdempotencyReservationTtl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "Idempotency reservation failed. TraceId={TraceId}", httpContext.TraceIdentifier);
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServiceUnavailable, "The request cannot be processed now; retry later.", ct);
            return true;
        }

        if (!reserved)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This idempotency key was already used.", ct);
            return true;
        }

        return false;
    }
}
