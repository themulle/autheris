namespace Autheris.Application.Sql;

using System;
using Autheris.Domain.Options;

/// <summary>
/// Row limit of one governed SQL execution: <see cref="DefaultMaxRows"/> applies without an explicit LIMIT,
/// <see cref="MaxAllowedRows"/> bounds an explicit LIMIT (0 = no bound).
/// </summary>
public sealed record SqlRowLimit(long DefaultMaxRows, long MaxAllowedRows)
{
    private const long FallbackDefaultMaxRows = 1000;

    /// <summary>The limits of <paramref name="channel"/>; unset values fall back to the WebSQL limits.</summary>
    public static SqlRowLimit For(WebSqlOptions webSql, ChannelRowLimitOptions? channel = null)
    {
        ArgumentNullException.ThrowIfNull(webSql);
        return new SqlRowLimit(
            channel?.DefaultMaxRows ?? webSql.DefaultMaxRows,
            channel?.MaxAllowedRows ?? webSql.MaxAllowedRows);
    }

    /// <summary>Rows the statement may return, given its explicit LIMIT (if any).</summary>
    public long Effective(long? explicitLimit)
    {
        if (explicitLimit is > 0)
        {
            return MaxAllowedRows > 0 ? Math.Min(explicitLimit.Value, MaxAllowedRows) : explicitLimit.Value;
        }

        long defaultLimit = DefaultMaxRows > 0 ? DefaultMaxRows : FallbackDefaultMaxRows;
        return MaxAllowedRows > 0 && defaultLimit > MaxAllowedRows ? MaxAllowedRows : defaultLimit;
    }
}
