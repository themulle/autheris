namespace Autheris.Api.Endpoints;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class DmlRequestGuard
{
    internal const string IdempotencyKeyHeader = "Idempotency-Key";
    internal const int MaxIdempotencyKeyLength = 128;
    private static readonly TimeSpan IdempotencyReservationTtl = TimeSpan.FromHours(24);

    public static Task<bool> RefuseDmlRequestAsync(
        HttpContext httpContext,
        string sql,
        bool dmlAllowed,
        TenantId tenantId,
        ILogger logger,
        CancellationToken ct,
        string keyPrefix = "websql-dml:")
    {
        if (!dmlAllowed)
        {
            return Task.FromResult(false);
        }

        var statementClass = SqlStatementClassifier.Classify(sql, logger);
        return RefuseClassifiedDmlRequestAsync(httpContext, statementClass, dmlAllowed, tenantId, logger, ct, keyPrefix);
    }

    public static Task<bool> RefuseClassifiedDmlRequestAsync(
        HttpContext httpContext,
        RequestStatementClass statementClass,
        bool dmlAllowed,
        TenantId tenantId,
        ILogger logger,
        CancellationToken ct,
        string keyPrefix = "websql-dml:")
    {
        if (!dmlAllowed)
        {
            return Task.FromResult(false);
        }

        return RefuseCoreAsync(httpContext, statementClass, tenantId, logger, keyPrefix, ct);
    }

    private static async Task<bool> RefuseCoreAsync(
        HttpContext httpContext,
        RequestStatementClass statementClass,
        TenantId tenantId,
        ILogger logger,
        string keyPrefix,
        CancellationToken ct)
    {
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
            await WebSqlEndpoints.WriteWebSqlErrorAsync(httpContext, new WebSqlPolicyException(
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
            || key.AsSpan().IndexOfAnyInRange('\0', ' ') >= 0 || key.Contains('\u007f', StringComparison.Ordinal))
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
        var reservationKey = keyPrefix + Convert.ToHexString(digest);
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
