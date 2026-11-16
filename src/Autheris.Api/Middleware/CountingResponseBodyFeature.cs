namespace Autheris.Api.Middleware;

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;

/// <summary>
/// Wraps <see cref="IHttpResponseBodyFeature"/> to count uncompressed application bytes written via
/// <see cref="Stream"/>, <see cref="Writer"/>, or <see cref="SendFileAsync"/> without altering flush or buffering semantics.
/// </summary>
internal sealed class CountingResponseBodyFeature : IHttpResponseBodyFeature, IDisposable
{
    private readonly IHttpResponseBodyFeature _inner;
    private readonly CountingStream _stream;
    private PipeWriter? _writer;
    private long _bytesWritten;

    public CountingResponseBodyFeature(IHttpResponseBodyFeature inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stream = new CountingStream(inner.Stream, AddBytes);
    }

    public void Dispose() => _stream.Dispose();

    public long BytesWritten => Volatile.Read(ref _bytesWritten);

    public Stream Stream => _stream;

    public PipeWriter Writer => _writer ??= new CountingPipeWriter(_inner.Writer, AddBytes);

    public void DisableBuffering() => _inner.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _inner.StartAsync(cancellationToken);

    public Task CompleteAsync() => _inner.CompleteAsync();

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        await _inner.SendFileAsync(path, offset, count, cancellationToken).ConfigureAwait(false);
        if (count.HasValue)
        {
            if (count.Value > 0)
            {
                AddBytes(count.Value);
            }
            return;
        }

        try
        {
            var length = new FileInfo(path).Length;
            var remaining = length - offset;
            if (remaining > 0)
            {
                AddBytes(remaining);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void AddBytes(long count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytesWritten, count);
        }
    }

    private sealed class CountingStream(Stream inner, Action<long> onBytes) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanTimeout => inner.CanTimeout;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int ReadTimeout
        {
            get => inner.ReadTimeout;
            set => inner.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => inner.WriteTimeout;
            set => inner.WriteTimeout = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) =>
            inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            onBytes(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            onBytes(buffer.Length);
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            onBytes(1);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteTrackedMemoryAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            WriteTrackedMemoryAsync(buffer, cancellationToken);

        private async ValueTask WriteTrackedMemoryAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            onBytes(buffer.Length);
        }

        // Wrapper stream must not close the underlying Kestrel/HTTP response stream when disposed.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }

    private sealed class CountingPipeWriter(PipeWriter inner, Action<long> onBytes) : PipeWriter
    {
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => inner.UnflushedBytes;

        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            onBytes(bytes);
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        [Obsolete("OnReaderCompleted is obsolete.")]
        public override void OnReaderCompleted(Action<Exception?, object?> callback, object? state) =>
            inner.OnReaderCompleted(callback, state);

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default) =>
            WriteTrackedAsync(source, cancellationToken);

        private async ValueTask<FlushResult> WriteTrackedAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            var result = await inner.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            onBytes(source.Length);
            return result;
        }
    }
}
