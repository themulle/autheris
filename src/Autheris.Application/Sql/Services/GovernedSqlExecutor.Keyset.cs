namespace Autheris.Application.Sql.Services;

using System;
using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Security;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Paging;
using Autheris.Domain.Common;

public sealed partial class GovernedSqlExecutor
{
    /// <summary>
    /// Wraps the outermost reader of a page whose statement is keyset-eligible, so the key values of the last delivered row can seed the
    /// next token. Null when the request is not a page that reports an outcome, the statement has no keyset shape, or an ordinal does not exist.
    /// </summary>
    private static KeysetCaptureReader? KeysetCaptureFor(GovernedSqlQueryRequest request, GovernedRewrite rewrite, DbDataReader inner)
    {
        if (request.Page is null || request.OnPageOutcome is null || rewrite.Compiled.KeysetKey is not { } key)
        {
            return null;
        }

        foreach (var ordinal in key.OutputOrdinals)
        {
            if (ordinal < 0 || ordinal >= inner.FieldCount)
            {
                return null;
            }
        }

        return new KeysetCaptureReader(inner, key);
    }

    /// <summary>
    /// The keyset token of the page that follows a truncated page, or null when none can be issued (the caller then applies the offset rules).
    /// Only the continuation that would pass the offset limit, or one that continues a keyset page, is a keyset token.
    /// </summary>
    private string? TryEncodeKeysetNextToken(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        KeysetCaptureReader? capture,
        long nextOffset,
        long maxOffset)
    {
        if (request.Keyset is null && nextOffset <= maxOffset)
        {
            return null;
        }

        if (capture is not { LastKeyValues: { } values } || request.Page is not { } page
            || capture.RowsRead != page.Size
            || capture.Key is not { } key
            || values.Length != key.Columns.Length
            || user.GetUserSid() == null)
        {
            return null;
        }

        return _pageTokenCodec.EncodeKeysetToken(
            request.Sql,
            request.Parameters,
            user,
            tenantId,
            PageTokenSourceBinding(request, tenantId),
            request.Transport ?? "WebSql",
            nextOffset,
            page.Size,
            page.Number + 1,
            key.ShapeHash,
            values,
            TimeSpan.FromMinutes(_options.Value.WebSql.Paging?.TokenLifetimeMinutes ?? 15));
    }
}
