namespace Autheris.Api.Middleware;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// SEC M-04: Response body stream used by <see cref="GatewayExtensibilityMiddleware"/> for egress interception.
/// Buffers at most <c>maxBufferBytes</c>. As soon as the limit would be exceeded, or the response turns out to be
/// a streaming response (text/event-stream, multipart/*, ndjson), the buffered prefix is flushed to the inner
/// stream and all further writes pass through unbuffered. The full response is therefore never collected in memory.
/// </summary>
internal sealed class BoundedResponseBufferStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponse? _response;
    private readonly long _maxBufferBytes;
    private readonly MemoryStream _buffer = new();

    public BoundedResponseBufferStream(Stream inner, long maxBufferBytes, HttpResponse? response = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferBytes);
        _inner = inner;
        _maxBufferBytes = maxBufferBytes;
        _response = response;
    }

    /// <summary>True when buffering was abandoned and the response is streamed directly to the client.</summary>
    public bool IsPassThrough { get; private set; }

    /// <summary>Reason why pass-through mode was entered (for logging).</summary>
    public string? PassThroughReason { get; private set; }

    public long BufferedLength => _buffer.Length;

    public byte[] GetBufferedBytes() => _buffer.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!IsPassThrough && ShouldSwitchToPassThrough(buffer.Length))
        {
            EnterPassThrough();
            _buffer.Position = 0;
            _buffer.CopyTo(_inner);
            _buffer.SetLength(0);
        }

        if (IsPassThrough)
        {
            _inner.Write(buffer);
        }
        else
        {
            _buffer.Write(buffer);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!IsPassThrough && ShouldSwitchToPassThrough(buffer.Length))
        {
            EnterPassThrough();
            if (_buffer.Length > 0)
            {
                await _inner.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), cancellationToken).ConfigureAwait(false);
            }
            _buffer.SetLength(0);
        }

        if (IsPassThrough)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _buffer.Write(buffer.Span);
        }
    }

    public override void Flush()
    {
        if (IsPassThrough)
        {
            _inner.Flush();
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        // While buffering, flushes are deferred until the egress phase writes the final body.
        return IsPassThrough ? _inner.FlushAsync(cancellationToken) : Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool ShouldSwitchToPassThrough(int incomingBytes)
    {
        if (_buffer.Length + incomingBytes > _maxBufferBytes)
        {
            PassThroughReason = "BufferLimitExceeded";
            return true;
        }

        if (_response != null && IsStreamingContentType(_response.ContentType))
        {
            PassThroughReason = "StreamingContentType";
            return true;
        }

        return false;
    }

    private void EnterPassThrough()
    {
        IsPassThrough = true;
    }

    internal static bool IsStreamingContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        return contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("application/x-ndjson", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("application/graphql-response+jsonl", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("application/jsonl", StringComparison.OrdinalIgnoreCase);
    }
}
