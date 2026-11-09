namespace Autheris.Api.Endpoints;

using System.Security.Cryptography;
using System.Text;
using Autheris.Api.Errors;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Domain.Security;
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

    internal enum RequestStatementClass { Read, Write, Other, Unparsable }

    /// <summary>
    /// Classifies by the parsed statement (the same analyzer the rewriter uses), never by text prefix.
    /// MERGE counts as a write; text that does not parse, or has trailing statements, is <see cref="RequestStatementClass.Unparsable"/>.
    /// </summary>
    internal static RequestStatementClass ClassifyStatement(string sql, ILogger logger)
    {
        try
        {
            var normalized = SqlParameterExtractor.NormalizeForAst(sql.Trim());
            var (tree, _) = FastSqlEngine.Default.Parse(normalized.AsMemory());
            if (tree.statement() is SqlBaseParser.MergeContext)
            {
                return RequestStatementClass.Write;
            }

            return new SqlQueryAnalyzer().Analyze(tree).StatementType switch
            {
                SqlStatementType.Select => RequestStatementClass.Read,
                SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete => RequestStatementClass.Write,
                _ => RequestStatementClass.Other
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Statement classification failed; the statement is treated as unparsable.");
            return RequestStatementClass.Unparsable;
        }
    }

    /// <summary>
    /// Request-level guard: the WebSQL route (when DML is enabled) admits a write once per (tenant, caller, key).
    /// Returns <c>true</c> when the request was refused (the response is written).
    /// </summary>
    private static async Task<bool> RefuseDmlRequestAsync(
        HttpContext httpContext, string sql, bool dmlAllowed, TenantId tenantId, ILogger logger, CancellationToken ct)
    {
        if (!dmlAllowed)
        {
            return false; // the executor's own "DML disabled" refusal answers first and nothing is reserved
        }

        var statementClass = ClassifyStatement(sql, logger);
        if (statementClass == RequestStatementClass.Unparsable)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                "The statement could not be parsed as a single statement.", ct);
            return true;
        }

        if (statementClass != RequestStatementClass.Write)
        {
            return false;
        }

        // The read-only marking wins over the scope; without the explicit scope no key is validated or reserved.
        if (httpContext.User.IsReadOnly() || !httpContext.User.HasScope(TokenAccessScope.WriteScope))
        {
            await WriteWebSqlErrorAsync(httpContext, new WebSqlPolicyException(
                "This token only permits read access; DML statements are rejected.",
                WebSqlPolicyException.ReadOnlyTokenCode), logger, ct);
            return true;
        }

        var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "A canonical user identity is required.", ct);
            return true;
        }

        var key = httpContext.Request.Headers[IdempotencyKeyHeader].ToString();
        if (key.Length is 0 or > MaxIdempotencyKeyLength || !Ascii.IsValid(key) || string.IsNullOrWhiteSpace(key)
            || key.AsSpan().IndexOfAnyInRange('\0', ' ') >= 0 || key.Contains('\u007f'))
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
