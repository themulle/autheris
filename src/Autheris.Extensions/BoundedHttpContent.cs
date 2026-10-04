namespace Autheris.Extensions;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// SEC K-X10: Standardized bounded response reading for all outbound integration HTTP clients:
/// rejects responses above <see cref="DefaultMaxResponseBytes"/> (or caller-specified limit),
/// both via Content-Length header and during chunked streaming.
/// </summary>
public static class BoundedHttpContent
{
    public const long DefaultMaxResponseBytes = 10 * 1024 * 1024; // 10 MB maximum payload cap

    public static async Task<string> ReadBoundedStringAsync(
        HttpResponseMessage response,
        string clientName,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maxBytes)
        {
            throw new InvalidOperationException(
                $"{clientName} response size ({declaredLength.Value} bytes) exceeds maximum allowed limit of {maxBytes} bytes.");
        }

        await using var stream = await ReadBoundedStreamAsync(response, clientName, maxBytes, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(((MemoryStream)stream).GetBuffer(), 0, (int)stream.Length);
    }

    public static async Task<MemoryStream> ReadBoundedStreamAsync(
        HttpResponseMessage response,
        string clientName,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maxBytes)
        {
            throw new InvalidOperationException(
                $"{clientName} response size ({declaredLength.Value} bytes) exceeds maximum allowed limit of {maxBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"{clientName} response exceeds maximum allowed limit of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
