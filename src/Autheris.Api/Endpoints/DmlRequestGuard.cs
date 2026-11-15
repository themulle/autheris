namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

internal sealed record DmlIdempotencyReservation(string Fingerprint);

internal sealed record DmlIdempotencyOutcome(int StatusCode, string ContentType, string Body);

internal sealed class DmlIdempotencyLease
{
    private readonly IIdempotencyStore _store;
    private readonly string _outcomeKey;
    private readonly TimeSpan _ttl;

    internal DmlIdempotencyLease(IIdempotencyStore store, string outcomeKey, TimeSpan ttl)
    {
        _store = store;
        _outcomeKey = outcomeKey;
        _ttl = ttl;
    }

    internal Task SaveOutcomeAsync(int statusCode, string contentType, string body, CancellationToken ct) =>
        _store.SetIfNotExistsAsync(_outcomeKey, new DmlIdempotencyOutcome(statusCode, contentType, body), _ttl, ct);
}

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

    internal static async Task<(bool Handled, DmlIdempotencyLease? Lease)> TryGuardDmlWithReplayAsync(
        HttpContext httpContext,
        string sql,
        string? dataSource,
        IReadOnlyDictionary<string, object?>? parameters,
        bool dmlAllowed,
        TenantId tenantId,
        int ttlSeconds,
        ILogger logger,
        CancellationToken ct,
        string keyPrefix = "websql-dml:")
    {
        if (!dmlAllowed)
        {
            return (false, null);
        }

        var statementClass = SqlStatementClassifier.Classify(sql, logger);
        var (valid, store, reservationKey) = await ValidateDmlCallerAndKeyAsync(
            httpContext, statementClass, tenantId, logger, keyPrefix, ct).ConfigureAwait(false);
        if (!valid)
        {
            return (true, null);
        }

        if (store is null || reservationKey is null)
        {
            return (false, null);
        }

        string fingerprint = ComputeRequestFingerprint(sql, dataSource, parameters);
        string outcomeKey = reservationKey + ":outcome";
        TimeSpan ttl = TimeSpan.FromSeconds(ttlSeconds > 0 ? ttlSeconds : (int)IdempotencyReservationTtl.TotalSeconds);

        try
        {
            DmlIdempotencyReservation? existing = await store.GetAsync<DmlIdempotencyReservation>(reservationKey, ct).ConfigureAwait(false);
            if (existing is null)
            {
                bool reserved = await store.SetIfNotExistsAsync(
                    reservationKey,
                    new DmlIdempotencyReservation(fingerprint),
                    ttl,
                    ct).ConfigureAwait(false);

                if (reserved)
                {
                    return (false, new DmlIdempotencyLease(store, outcomeKey, ttl));
                }

                existing = await store.GetAsync<DmlIdempotencyReservation>(reservationKey, ct).ConfigureAwait(false);
            }

            if (existing is null || !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                await ApiProblems.WriteAsync(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "This idempotency key was already used for a different statement.",
                    ct).ConfigureAwait(false);
                return (true, null);
            }

            DmlIdempotencyOutcome? outcome = await store.GetAsync<DmlIdempotencyOutcome>(outcomeKey, ct).ConfigureAwait(false);
            if (outcome is null)
            {
                await ApiProblems.WriteAsync(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "This idempotency key was already used.",
                    ct).ConfigureAwait(false);
                return (true, null);
            }

            httpContext.Response.StatusCode = outcome.StatusCode;
            httpContext.Response.ContentType = outcome.ContentType;
            httpContext.Response.Headers["X-Idempotency-Replayed"] = "true";
            await httpContext.Response.WriteAsync(outcome.Body, ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "Idempotency reservation failed. TraceId={TraceId}", httpContext.TraceIdentifier);
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServiceUnavailable, "The request cannot be processed now; retry later.", ct).ConfigureAwait(false);
            return (true, null);
        }
    }

    internal static string ComputeRequestFingerprint(
        string sql,
        string? dataSource,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var sb = new StringBuilder();
        sb.Append(dataSource ?? string.Empty);
        sb.Append('\0');
        sb.Append(sql);
        sb.Append('\0');
        if (parameters is { Count: > 0 })
        {
            foreach (var kvp in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append(kvp.Key);
                sb.Append('=');
                sb.Append(JsonSerializer.Serialize(kvp.Value));
                sb.Append('\n');
            }
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(digest);
    }

    private static async Task<bool> RefuseCoreAsync(
        HttpContext httpContext,
        RequestStatementClass statementClass,
        TenantId tenantId,
        ILogger logger,
        string keyPrefix,
        CancellationToken ct)
    {
        var (valid, store, reservationKey) = await ValidateDmlCallerAndKeyAsync(
            httpContext, statementClass, tenantId, logger, keyPrefix, ct).ConfigureAwait(false);
        if (!valid)
        {
            return true;
        }

        if (store is null || reservationKey is null)
        {
            return false;
        }

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

    private static async Task<(bool Proceed, IIdempotencyStore? Store, string? ReservationKey)> ValidateDmlCallerAndKeyAsync(
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
            return (false, null, null);
        }

        if (statementClass != RequestStatementClass.Write)
        {
            return (true, null, null);
        }

        // The read-only marking wins over the scope; without the explicit scope no key is validated or reserved.
        if (httpContext.User.IsReadOnly() || !httpContext.User.HasScope(TokenAccessScope.WriteScope))
        {
            await WebSqlEndpoints.WriteWebSqlErrorAsync(httpContext, new WebSqlPolicyException(
                "This token only permits read access; DML statements are rejected.",
                WebSqlPolicyException.ReadOnlyTokenCode), logger, ct);
            return (false, null, null);
        }

        var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "A canonical user identity is required.", ct);
            return (false, null, null);
        }

        var key = httpContext.Request.Headers[IdempotencyKeyHeader].ToString();
        if (key.Length is 0 or > MaxIdempotencyKeyLength || !Ascii.IsValid(key) || string.IsNullOrWhiteSpace(key)
            || key.AsSpan().IndexOfAnyInRange('\0', ' ') >= 0 || key.Contains('\u007f', StringComparison.Ordinal))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                $"idempotency_key_required: Data-changing statements require an '{IdempotencyKeyHeader}' header of 1 to {MaxIdempotencyKeyLength} ASCII characters.", ct);
            return (false, null, null);
        }

        var store = httpContext.RequestServices?.GetService<IIdempotencyStore>();
        if (store == null)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServiceUnavailable, "The request cannot be processed now; retry later.", ct);
            return (false, null, null);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId.Value}\0{callerIdentity}\0{key}"));
        var reservationKey = keyPrefix + Convert.ToHexString(digest);
        return (true, store, reservationKey);
    }
}
