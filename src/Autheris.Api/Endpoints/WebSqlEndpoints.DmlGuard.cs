namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

public static partial class WebSqlEndpoints
{
    internal const string IdempotencyKeyHeader = DmlRequestGuard.IdempotencyKeyHeader;
    internal const int MaxIdempotencyKeyLength = DmlRequestGuard.MaxIdempotencyKeyLength;

    internal enum RequestStatementClass { Read, Write, Other, Unparsable }

    /// <summary>
    /// Classifies by the parsed statement via <see cref="SqlStatementClassifier"/>, never by text prefix.
    /// </summary>
    internal static RequestStatementClass ClassifyStatement(string sql, ILogger logger) =>
        SqlStatementClassifier.Classify(sql, logger) switch
        {
            Autheris.Application.Sql.RequestStatementClass.Read => RequestStatementClass.Read,
            Autheris.Application.Sql.RequestStatementClass.Write => RequestStatementClass.Write,
            Autheris.Application.Sql.RequestStatementClass.Other => RequestStatementClass.Other,
            _ => RequestStatementClass.Unparsable
        };

    /// <summary>
    /// Request-level guard with replay/conflict semantics for WebSQL DML statements.
    /// </summary>
    private static Task<(bool Handled, DmlIdempotencyLease? Lease)> TryGuardDmlRequestAsync(
        HttpContext httpContext,
        string sql,
        string? dataSource,
        IReadOnlyDictionary<string, object?>? parameters,
        bool dmlAllowed,
        TenantId tenantId,
        int ttlSeconds,
        ILogger logger,
        CancellationToken ct) =>
        DmlRequestGuard.TryGuardDmlWithReplayAsync(
            httpContext,
            sql,
            dataSource,
            parameters,
            dmlAllowed,
            tenantId,
            ttlSeconds,
            logger,
            ct);
}
