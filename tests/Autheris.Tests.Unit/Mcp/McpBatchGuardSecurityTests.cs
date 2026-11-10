namespace Autheris.Tests.Unit.Mcp;

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpBatchGuardSecurityTests
{
    [Fact]
    public async Task Post_WithOver512BytesWhitespaceFollowedByBatch_IsRejectedWith400()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        var padding = new string(' ', 1024);
        var payload = padding + """[{"jsonrpc":"2.0","id":1,"method":"tools/list"}]""";
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        bool innerCalled = false;
        await McpEndpoints.ExecuteBatchGuardAsync(context, _ =>
        {
            innerCalled = true;
            return Task.CompletedTask;
        });

        innerCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        responseStream.Position = 0;
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()!
            .ShouldContain("JSON-RPC batching is not supported");
    }

    [Fact]
    public async Task Post_WithOver512BytesWhitespaceFollowedBySingleRequest_IsAccepted()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        var padding = new string(' ', 800);
        var payload = padding + """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        bool innerCalled = false;
        string? bodyInInner = null;

        await McpEndpoints.ExecuteBatchGuardAsync(context, async ctx =>
        {
            innerCalled = true;
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            bodyInInner = await reader.ReadToEndAsync();
        });

        innerCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        bodyInInner.ShouldBe(payload);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithOver512BytesWhitespaceFollowedByBracket_ReturnsTrue()
    {
        var padding = new string(' ', 700);
        var payload = padding + "[]";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(stream);

        isBatch.ShouldBeTrue();
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithOver512BytesWhitespaceFollowedByBrace_ReturnsFalse()
    {
        var padding = new string(' ', 700);
        var payload = padding + "{}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(stream);

        isBatch.ShouldBeFalse();
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithUtf8BomAndOver512BytesWhitespace_DetectsBatch()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var padding = Encoding.UTF8.GetBytes(new string(' ', 600));
        var content = Encoding.UTF8.GetBytes("[1, 2, 3]");

        using var stream = new MemoryStream();
        stream.Write(bom);
        stream.Write(padding);
        stream.Write(content);
        stream.Position = 0;

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(stream);

        isBatch.ShouldBeTrue();
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithMixedWhitespace_DetectsBatch()
    {
        var whitespace = "\t\r\n   \t  \r\n";
        var repeated = string.Concat(System.Linq.Enumerable.Repeat(whitespace, 60)); // ~660 chars
        var payload = repeated + "[{\"test\":1}]";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(stream);

        isBatch.ShouldBeTrue();
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithOnlyWhitespace_ReturnsFalse()
    {
        var whitespace = new string(' ', 1200);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(whitespace));

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(stream);

        isBatch.ShouldBeFalse();
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithEmptyStream_ReturnsFalse()
    {
        using var stream = new MemoryStream();

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(stream);

        isBatch.ShouldBeFalse();
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task IsBatchPayloadAsync_WithChunkedWhitespaceStreams_DetectsBatch()
    {
        var payload = new string(' ', 600) + "[]";
        using var chunkedStream = new ChunkedReadStream(Encoding.UTF8.GetBytes(payload), maxChunkSize: 64);

        bool isBatch = await McpEndpoints.IsBatchPayloadAsync(chunkedStream);

        isBatch.ShouldBeTrue();
        chunkedStream.Position.ShouldBe(0);
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxChunkSize;
        private int _position;

        public ChunkedReadStream(byte[] data, int maxChunkSize)
        {
            _data = data;
            _maxChunkSize = maxChunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _data.Length) return 0;
            int toRead = Math.Min(Math.Min(buffer.Length, _maxChunkSize), _data.Length - _position);
            _data.AsSpan(_position, toRead).CopyTo(buffer);
            _position += toRead;
            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) =>
            _position = origin switch
            {
                SeekOrigin.Begin => (int)offset,
                SeekOrigin.Current => _position + (int)offset,
                SeekOrigin.End => _data.Length + (int)offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
