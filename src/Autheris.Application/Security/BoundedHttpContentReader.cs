namespace Autheris.Application.Security;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// SR-L-16: reads an HTTP response body with a hard byte cap. Fails closed on a declared oversize
/// Content-Length and enforces the cap while streaming (chunked responses without Content-Length).
/// </summary>
public static class BoundedHttpContentReader
{
    public static async Task<string> ReadStringAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var declared = content.Headers.ContentLength;
        if (declared is > 0 && declared.Value > maxBytes)
        {
            throw new ArgumentException("The remote document exceeds the maximum allowed size.");
        }

        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new ArgumentException("The remote document exceeds the maximum allowed size.");
                }

                buffer.Write(chunk, 0, read);
            }

            return new UTF8Encoding(false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    public static async Task<MemoryStream> ReadBoundedStreamAsync(
        HttpResponseMessage response,
        string clientName,
        long maxBytes,
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
