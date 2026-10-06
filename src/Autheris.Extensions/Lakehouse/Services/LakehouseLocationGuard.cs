namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;

/// <summary>
/// Shared validation helpers for Lakehouse storage locations (traversal, bucket allowlists, bounded reads).
/// </summary>
internal static class LakehouseLocationGuard
{
    internal const long DefaultMaxReadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Returns true when the (optionally percent-encoded) path contains a '.' or '..' segment or a NUL character.
    /// </summary>
    internal static bool ContainsTraversal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains('\0')) return true;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return true;
        }

        if (decoded.Contains('\0')) return true;

        foreach (var segment in decoded.Split('/', '\\'))
        {
            if (segment == ".." || segment == ".")
            {
                return true;
            }
        }

        return false;
    }

    internal static void EnsureNoTraversal(string? path, string location)
    {
        if (ContainsTraversal(path))
        {
            throw new System.Security.SecurityException($"Path traversal segments are not permitted in lakehouse location '{location}'.");
        }
    }

    /// <summary>
    /// SEC EX-10: Reject '?', '#' and '%' in storage keys to prevent query/fragment/encoded path traversal injection.
    /// </summary>
    internal static void EnsureSafeStorageKey(string? key, string location)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (key.Contains('?') || key.Contains('#') || key.Contains('%'))
        {
            throw new System.Security.SecurityException($"Forbidden character ('?', '#' or '%') in storage key '{key}' in location '{location}'.");
        }

        EnsureNoTraversal(key, location);
    }

    /// <summary>
    /// SEC E-15: row-level tenant check applied to every returned row, independent of file pruning.
    /// A row whose tenant value is present and differs (ordinal, case-sensitive) from the session tenant is not returned.
    /// </summary>
    internal static bool RowBelongsToTenant(IReadOnlyDictionary<string, object?> row, string tenantColumn, string tenantId)
    {
        foreach (var kvp in row)
        {
            if (string.Equals(kvp.Key, tenantColumn, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value is string s
                    ? string.Equals(s, tenantId, StringComparison.Ordinal)
                    : string.Equals(kvp.Value?.ToString(), tenantId, StringComparison.Ordinal);
            }
        }

        // No tenant value on the row: the file-level proof (mandatory partition/statistics evidence) is the only evidence.
        return true;
    }

    internal static long ResolveMaxReadBytes(GatewayOptions? options)
    {
        var configured = options?.Lakehouse?.Storage?.MaxReadBytes ?? DefaultMaxReadBytes;
        return configured > 0 ? configured : DefaultMaxReadBytes;
    }

    /// <summary>
    /// Buckets that may be addressed: the configured S3 bucket plus the buckets of all configured s3:// / minio:// table locations.
    /// An empty set means no bucket restriction has been configured.
    /// </summary>
    internal static HashSet<string> GetAllowedBuckets(GatewayOptions options)
    {
        var buckets = new HashSet<string>(StringComparer.Ordinal);
        var storage = options.Lakehouse.Storage;
        if (!string.IsNullOrWhiteSpace(storage.S3Bucket))
        {
            buckets.Add(storage.S3Bucket.Trim());
        }

        foreach (var table in options.Lakehouse.Tables.Values)
        {
            var loc = table.Location;
            if (string.IsNullOrWhiteSpace(loc)) continue;

            string? raw = null;
            if (loc.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            {
                raw = loc[5..];
            }
            else if (loc.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
            {
                raw = loc[8..];
            }

            if (raw == null) continue;
            var slash = raw.IndexOf('/');
            var bucket = slash >= 0 ? raw[..slash] : raw;
            if (!string.IsNullOrWhiteSpace(bucket))
            {
                buckets.Add(bucket);
            }
        }

        return buckets;
    }

    internal static async ValueTask<string> ReadBoundedTextAsync(HttpContent content, long maxBytes, string location, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxBytes)
        {
            throw new InvalidDataException($"Lakehouse object '{location}' ({content.Headers.ContentLength.Value} bytes) exceeds the maximum allowed size of {maxBytes} bytes.");
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await ReadBoundedTextAsync(stream, maxBytes, location, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async ValueTask<string> ReadBoundedTextAsync(Stream stream, long maxBytes, string location, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new InvalidDataException($"Lakehouse object '{location}' exceeds the maximum allowed size of {maxBytes} bytes.");
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
