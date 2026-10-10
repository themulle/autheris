namespace Autheris.Application.Services;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Exceptions;

/// <summary>
/// F-GOV-14 (WI-20, SEC-17): caps the response body of the declarative HTTP client. A declared <c>Content-Length</c>
/// above the cap aborts before anything is read; otherwise the body stream aborts as soon as more than the cap was
/// handed out (at most the cap plus one read buffer), so a hostile or broken upstream cannot make the gateway buffer a
/// multi-gigabyte document. The overflow is a <see cref="GatewaySecurityException"/> (<c>ROW_LIMIT_EXCEEDED</c>);
/// the executor never sees a partial body.
/// </summary>
public sealed class ResponseSizeLimitingHandler : DelegatingHandler
{
    public const string ErrorCode = "ROW_LIMIT_EXCEEDED";

    private readonly long _maxBytes;

    public ResponseSizeLimitingHandler(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content == null)
        {
            return response;
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > _maxBytes)
        {
            response.Dispose();
            throw Exceeded(_maxBytes);
        }

        response.Content = new LimitedContent(response.Content, _maxBytes);
        return response;
    }

    /// <summary>
    /// The bytes a response body stream has handed out so far: the length of a seekable stream, the running count of a
    /// capped body stream, otherwise 0. <see cref="Stream.Length"/> of a non-seekable stream throws by contract (CR-09).
    /// </summary>
    internal static long BytesRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream is LimitedStream limited ? limited.BytesRead : stream.CanSeek ? stream.Length : 0;
    }

    private static GatewaySecurityException Exceeded(long max) =>
        new($"The upstream response exceeds the maximum allowed size of {max} bytes.", ErrorCode);

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _max;

        public LimitedContent(HttpContent inner, long max)
        {
            _inner = inner;
            _max = max;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new LimitedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _max);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new LimitedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _max);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            new LimitedStream(_inner.ReadAsStream(cancellationToken), _max);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var source = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await source.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Read-only pass-through stream that throws once more than the cap was read.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        /// <summary>The bytes read so far; <see cref="Stream.Length"/> and <see cref="Stream.Position"/> stay unsupported (CanSeek is false).</summary>
        public long BytesRead => _read;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > max)
            {
                throw Exceeded(max);
            }

            return read;
        }
    }
}
