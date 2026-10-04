namespace Autheris.Api.Extensions;

using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

/// <summary>
/// SEC M-07: Body size enforcement that does not rely on <c>Content-Length</c> alone.
/// Chunked requests (no Content-Length) are read with a hard byte cap, and the server-side
/// limit is tightened via <see cref="IHttpMaxRequestBodySizeFeature"/> where still possible.
/// </summary>
public static class RequestBodyLimitExtensions
{
    private const int ReadChunkSize = 16 * 1024;

    /// <summary>
    /// SEC M-01/M-07: Raises (or lowers) the Kestrel body limit for a single endpoint. The global limit
    /// (Gateway:Hosting:MaxRequestBodySizeBytes, default 2 MB) cannot be raised from inside the handler once
    /// routing has run, so endpoints accepting larger payloads must declare the limit as endpoint metadata
    /// (applied by the routing middleware via <c>IRequestSizeLimitMetadata</c> before the body is read).
    /// </summary>
    public static TBuilder WithRequestBodyLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return builder.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(maxBytes));
    }

    /// <summary>
    /// Tightens the server-side request body limit for the current request (if the feature is
    /// available and not yet read-only) and rejects a declared Content-Length above <paramref name="maxBytes"/>.
    /// </summary>
    /// <exception cref="BadHttpRequestException">Status 413 when the declared length exceeds the limit.</exception>
    public static void ApplyMaxRequestBodySize(this HttpRequest request, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false } &&
            (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > maxBytes))
        {
            feature.MaxRequestBodySize = maxBytes;
        }

        if (request.ContentLength is long declared && declared > maxBytes)
        {
            throw new BadHttpRequestException(
                $"Request body exceeds the maximum allowed size of {maxBytes} bytes.",
                StatusCodes.Status413PayloadTooLarge);
        }
    }

    /// <summary>
    /// Reads the request body as UTF-8 text, never buffering more than <paramref name="maxBytes"/> bytes,
    /// regardless of whether the client sent a Content-Length or used chunked transfer encoding.
    /// </summary>
    /// <exception cref="BadHttpRequestException">Status 413 when the body exceeds the limit.</exception>
    public static async Task<string> ReadBodyAsStringAsync(this HttpRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        var bytes = await request.ReadBodyAsBytesAsync(maxBytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads the request body into a byte array, never buffering more than <paramref name="maxBytes"/> bytes.
    /// </summary>
    /// <exception cref="BadHttpRequestException">Status 413 when the body exceeds the limit.</exception>
    public static async Task<byte[]> ReadBodyAsBytesAsync(this HttpRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        request.ApplyMaxRequestBodySize(maxBytes);

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkSize];
        long total = 0;

        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new BadHttpRequestException(
                    $"Request body exceeds the maximum allowed size of {maxBytes} bytes.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
