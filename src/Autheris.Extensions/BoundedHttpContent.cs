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

    public static Task<MemoryStream> ReadBoundedStreamAsync(
        HttpResponseMessage response,
        string clientName,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken ct = default) =>
        Autheris.Application.Security.BoundedHttpContentReader.ReadBoundedStreamAsync(response, clientName, maxBytes, ct);
}
