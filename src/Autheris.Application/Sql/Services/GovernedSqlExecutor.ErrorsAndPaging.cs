namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.ListParameters;
using Autheris.Application.Sql.Paging;
using Autheris.Application.Sql.Synthetic;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;


public sealed partial class GovernedSqlExecutor
{
    /// <summary>
    /// True when the statement binds a client list or a caller attribute value (BC-129-16): a driver message can quote such a value, so
    /// the exception text is not logged for it.
    /// </summary>
    internal static bool BindsValueFromTheCaller(CompiledSql compiled, IReadOnlyDictionary<string, object?> clientValues) =>
        clientValues.Values.Any(v => v is ClientListValue) ||
        compiled.Parameters.Any(p => p.Origin == ParameterOrigin.Policy && p.SourceName != null && Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(p.SourceName));

    /// <summary>
    /// A driver error becomes a typed error with a stable code and fixed message. Timeouts, unavailability and drift
    /// are classified first (504, 503, 502). Non-driver errors pass through unchanged.
    /// </summary>
    private GovernedSqlException? SanitizeProviderError(Exception error, TargetSqlDialect dialect, string dataSourceName, bool clientListsBound)
    {
        if (error is GovernedSqlException || !IsProviderError(error))
        {
            return null;
        }

        var classified = DataAccessErrorClassifier.Classify(error);
        if (classified is DataAccessErrorKind.Timeout or DataAccessErrorKind.Unavailable) return null;

        var mapped = DmlErrorSanitizer.Map(dialect, error);
        if (mapped.Code == GovernedSqlErrorCodes.ProviderError && classified == DataAccessErrorKind.StatementRejected)
            mapped = new GovernedSqlException(GovernedSqlErrorCodes.StatementRejected, dialect);

        // a recursion limit (SQL Server 530, Oracle ORA-32044) is logged by type and number only, never with the driver message.
        if (clientListsBound || mapped.Code == GovernedSqlErrorCodes.LimitExceeded)
        {
            // 07 / BC-129-16: driver messages can carry bound values; exception is not logged when client lists are bound.
            var driver = error as DbException;
            _logger?.LogWarning("WebSQL provider error on data source {DataSource} ({Dialect}); the caller receives a sanitized error. ErrorType={ErrorType} ErrorCode={ErrorCode} TraceId={TraceId}",
                dataSourceName, dialect, error.GetType().Name, driver?.ErrorCode, Activity.Current?.TraceId.ToString());
        }
        else
        {
            _logger?.LogWarning(error, "WebSQL provider error on data source {DataSource} ({Dialect}); the caller receives a sanitized error. TraceId={TraceId}",
                dataSourceName, dialect, Activity.Current?.TraceId.ToString());
        }

        return mapped;
    }

    private static bool IsProviderError(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is DbException)
            {
                return true;
            }
        }

        return false;
    }

    private static string ErrorCodeOf(Exception error) =>
        SqlCompilerErrorMapper.TryMap(error, out var mapping) ? mapping.Code : "UNMAPPED";

    /// <summary>The audit reason of a rejected statement: the gateway's own policy message, or the stable code of an engine rejection.</summary>
    private static string DeniedReason(Exception error) => error switch
    {
        WebSqlPolicyException => error.Message,
        SecurityException => ErrorCodeOf(error),
        _ => SqlCompilerErrorMapper.TryMap(error, out var mapping) ? mapping.Code : BadRequestCode
    };

    /// <summary>The audit code of an <see cref="ArgumentException"/> that no mapper rule owns (an empty or oversized statement).</summary>
    private const string BadRequestCode = "SQL_BAD_REQUEST";

    /// <summary>
    /// L-4: what the compile half refuses and audits before it rethrows: every security exception, and every exception that the error
    /// mapper owns or that is a bad request (<see cref="ArgumentException"/>). A client that cancelled is not a rejection.
    /// </summary>
    private static bool IsAuditedRejection(Exception error, CancellationToken ct) =>
        error is SecurityException ||
        (!(error is OperationCanceledException && ct.IsCancellationRequested) &&
         (error is ArgumentException || SqlCompilerErrorMapper.TryMap(error, out _)));

    /// <summary>
    /// The data source a page token binds to. It is derived from the request (the named source, else the configured default), never from
    /// the rewrite's resolved source, so the request that continues a page verifies against the same value the issuing request bound.
    /// </summary>
    private string PageTokenSourceBinding(GovernedSqlQueryRequest request, TenantId tenantId) =>
        string.IsNullOrWhiteSpace(request.DataSourceName)
            ? _options.Value.WebSql.DefaultDataSourceName
            : _dataSourceResolver.ResolveAllowedDataSource(request.DataSourceName, tenantId);

    private void NotifyPageOutcome(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, GovernedRewrite? rewrite, bool isTruncated, KeysetCaptureReader? keysetCapture = null)
    {
        if (request.Page == null || request.OnPageOutcome == null)
        {
            return;
        }

        var pageResponse = new GovernedSqlPageResponse(request.Page.Number, request.Page.Offset, request.Page.Size);
        string? nextToken = null;
        string? pagingUnavailable = rewrite?.PagingUnavailable;
        if (pagingUnavailable == null && isTruncated)
        {
            long nextOffset = checked(request.Page.Offset + request.Page.Size);
            long maxOffset = _options.Value.WebSql.Paging?.MaxOffset ?? 10_000;
            if (TryEncodeKeysetNextToken(request, user, tenantId, keysetCapture, nextOffset, maxOffset) is { } keysetToken)
            {
                nextToken = keysetToken;
            }
            else if (nextOffset > maxOffset)
            {
                pagingUnavailable = "offset_limit";
            }
            else if (user.GetUserSid() == null)
            {
                pagingUnavailable = "caller_not_bindable";
            }
            else
            {
                nextToken = EncodePageToken(request, user, tenantId, nextOffset);
            }
        }
        Func<int, string?>? resumeAfter = null;
        if (rewrite?.PagingUnavailable == null)
        {
            long maxResumeOffset = _options.Value.WebSql.Paging?.MaxOffset ?? 10_000;
            resumeAfter = rowsKept =>
            {
                if (rowsKept < 1
                    || rowsKept >= request.Page.Size
                    || request.Page.Offset + rowsKept > maxResumeOffset
                    || user.GetUserSid() == null)
                {
                    return null;
                }

                try
                {
                    return EncodePageToken(request, user, tenantId, request.Page.Offset + rowsKept);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
                {
                    return null;
                }
            };
        }

        request.OnPageOutcome(new GovernedSqlPageOutcome(pageResponse, nextToken, pagingUnavailable, resumeAfter));
    }

    /// <summary>Signs a continuation token for <paramref name="offset"/> with the binding every page token of this request carries.</summary>
    private string EncodePageToken(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, long offset) =>
        _pageTokenCodec.EncodeToken(
            request.Sql,
            request.Parameters,
            user,
            tenantId,
            PageTokenSourceBinding(request, tenantId),
            request.Transport ?? "WebSql",
            offset,
            request.Page!.Size,
            checked((int)((offset / request.Page.Size) + 1)),
            TimeSpan.FromMinutes(_options.Value.WebSql.Paging?.TokenLifetimeMinutes ?? 15));

}
