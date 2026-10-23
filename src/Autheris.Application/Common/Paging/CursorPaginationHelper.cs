namespace Autheris.Application.Common.Paging;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Standard Relay PageInfo model.
/// </summary>
public sealed record PageInfo(
    bool HasNextPage,
    bool HasPreviousPage,
    string? StartCursor,
    string? EndCursor
);

/// <summary>
/// Paged connection result matching GraphQL Relay standard.
/// </summary>
public sealed record ConnectionResult<T>(
    IReadOnlyList<Edge<T>> Edges,
    IReadOnlyList<T> Nodes,
    PageInfo PageInfo,
    int TotalCount,
    bool IsTruncated = false
);

/// <summary>
/// Individual edge with item node and opaque cursor.
/// </summary>
public sealed record Edge<T>(
    T Node,
    string Cursor
);

/// <summary>
/// High-performance helper for encoding, decoding, and slicing Relay cursor pagination.
/// </summary>
public static class CursorPaginationHelper
{
    private const string CursorPrefix = "cursor:offset:";

    /// <summary>
    /// Encodes a zero-based offset into an opaque, URL-safe Base64 cursor.
    /// </summary>
    public static string EncodeCursor(int offset)
    {
        var raw = $"{CursorPrefix}{offset}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Decodes an opaque cursor string into a zero-based offset. Returns 0 if cursor is null or empty.
    /// Throws ArgumentException if cursor is invalid or malformed.
    /// </summary>
    public static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!raw.StartsWith(CursorPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid cursor format: missing expected prefix.", nameof(cursor));
            }

            var offsetStr = raw[CursorPrefix.Length..];
            if (!int.TryParse(offsetStr, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                throw new ArgumentException("Invalid cursor offset value.", nameof(cursor));
            }

            return offset;
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid cursor format: not a valid base64 string.", nameof(cursor));
        }
    }

    /// <summary>
    /// Slices an in-memory list according to Relay cursor parameters, enforcing max limits and reporting truncation.
    /// </summary>
    public static ConnectionResult<T> ApplyPagination<T>(
        IReadOnlyList<T> source,
        int? first,
        string? after,
        int defaultPageSize = 50,
        int maxAllowedPageSize = 500)
    {
        ArgumentNullException.ThrowIfNull(source);

        var requestedFirst = first ?? defaultPageSize;
        if (requestedFirst < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(first), "The 'first' argument must be non-negative.");
        }

        var isTruncated = false;
        if (requestedFirst > maxAllowedPageSize)
        {
            requestedFirst = maxAllowedPageSize;
            isTruncated = true;
        }

        var startOffset = 0;
        if (!string.IsNullOrWhiteSpace(after))
        {
            // The 'after' cursor points to the item *after* which to begin. MISC-15: long arithmetic, so int.MaxValue cannot wrap.
            startOffset = (int)Math.Min((long)DecodeCursor(after) + 1, source.Count);
        }

        var availableItems = source.Count - startOffset;
        var takeCount = Math.Min(requestedFirst, availableItems);

        var edges = new List<Edge<T>>(takeCount);
        var nodes = new List<T>(takeCount);

        for (var i = 0; i < takeCount; i++)
        {
            var currentIndex = startOffset + i;
            var item = source[currentIndex];
            nodes.Add(item);
            edges.Add(new Edge<T>(item, EncodeCursor(currentIndex)));
        }

        var hasNextPage = (startOffset + takeCount) < source.Count || isTruncated;
        var hasPreviousPage = startOffset > 0;

        var startCursor = edges.Count > 0 ? edges[0].Cursor : null;
        var endCursor = edges.Count > 0 ? edges[^1].Cursor : null;

        var pageInfo = new PageInfo(
            HasNextPage: hasNextPage,
            HasPreviousPage: hasPreviousPage,
            StartCursor: startCursor,
            EndCursor: endCursor
        );

        return new ConnectionResult<T>(
            Edges: edges,
            Nodes: nodes,
            PageInfo: pageInfo,
            TotalCount: source.Count,
            IsTruncated: isTruncated
        );
    }
}
