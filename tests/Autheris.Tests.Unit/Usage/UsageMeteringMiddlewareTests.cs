namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;
using Xunit;

public sealed class UsageMeteringMiddlewareTests
{
    private sealed class TestResponseBodyFeature : IHttpResponseBodyFeature, IDisposable
    {
        private readonly MemoryStream _backing = new();
        private readonly PipeWriter _writer;

        public TestResponseBodyFeature()
        {
            _writer = PipeWriter.Create(_backing, new StreamPipeWriterOptions(leaveOpen: true));
        }

        public void Dispose()
        {
            _writer.Complete();
            _backing.Dispose();
        }

        public Stream Stream => _backing;
        public PipeWriter Writer => _writer;
        public int FlushCalls { get; private set; }
        public long FlushedBytesOnFirstFlush { get; private set; }

        public void DisableBuffering() { }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task CompleteAsync()
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
            await _backing.FlushAsync().ConfigureAwait(false);
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public byte[] WrittenBytes => _backing.ToArray();

        public async Task RecordFlushAsync()
        {
            await _backing.FlushAsync().ConfigureAwait(false);
            FlushCalls++;
            if (FlushCalls == 1)
            {
                FlushedBytesOnFirstFlush = _backing.Length;
            }
        }
    }

    private sealed class FlushObservingStream(Stream inner, Func<Task> onFlush) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            await onFlush().ConfigureAwait(false);
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed class StreamingBodyFeature(TestResponseBodyFeature inner) : IHttpResponseBodyFeature, IDisposable
    {
        private readonly Stream _observingStream = new FlushObservingStream(inner.Stream, inner.RecordFlushAsync);
        public Stream Stream => _observingStream;
        public PipeWriter Writer => inner.Writer;
        public void Dispose() => _observingStream.Dispose();
        public void DisableBuffering() => inner.DisableBuffering();
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public Task CompleteAsync() => inner.CompleteAsync();
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            inner.SendFileAsync(path, offset, count, cancellationToken);
    }

    private static SecurityPrincipalContext AuthenticatedContext() => new()
    {
        UserSid = new Sid("S-1-5-21-USER-1"),
        ResolvedTenant = new TenantId("tenant-1"),
        GroupSids = new HashSet<Sid> { new("S-1-5-21-GRP-1") },
        TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        AuthenticationScheme = "Bearer",
        IsAuthenticated = true
    };

    [Fact]
    public async Task Counts_stream_and_pipewriter_bytes()
    {
        var aggregator = new UsageAggregator();
        using var bodyFeature = new TestResponseBodyFeature();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tables/orders/stream";
        context.Features.Set<IHttpResponseBodyFeature>(bodyFeature);
        context.Items[SecurityPrincipalContext.ItemKey] = AuthenticatedContext();

        var streamPayload = Encoding.UTF8.GetBytes("{\"row\":1}\n");
        var pipePayload = Encoding.UTF8.GetBytes("{\"row\":2}\n");

        var middleware = new UsageMeteringMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(streamPayload);
            var mem = ctx.Response.BodyWriter.GetMemory(pipePayload.Length);
            pipePayload.CopyTo(mem);
            ctx.Response.BodyWriter.Advance(pipePayload.Length);
            await ctx.Response.BodyWriter.FlushAsync();
        });

        await middleware.InvokeAsync(context, aggregator);

        var snapshot = aggregator.Swap();
        var entry = snapshot.Series.ShouldHaveSingleItem();
        entry.Value.Requests.ShouldBe(1);
        entry.Value.Bytes.ShouldBe(streamPayload.Length + pipePayload.Length);
        entry.Value.Allow.ShouldBe(1);
    }

    [Fact]
    public async Task Measures_duration()
    {
        var aggregator = new UsageAggregator();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tables";
        context.Items[SecurityPrincipalContext.ItemKey] = AuthenticatedContext();

        var middleware = new UsageMeteringMiddleware(ctx =>
        {
            var sw = Stopwatch.StartNew();
            SpinWait.SpinUntil(() => sw.ElapsedMilliseconds >= 4);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, aggregator);

        var entry = aggregator.Swap().Series.ShouldHaveSingleItem();
        entry.Value.DurationMs.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/alive")]
    [InlineData("/ready")]
    [InlineData("/metrics")]
    public async Task Skips_health_and_metrics(string path)
    {
        var aggregator = new UsageAggregator();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Items[SecurityPrincipalContext.ItemKey] = AuthenticatedContext();
        UsageRequestScope? observedScope = null;

        var middleware = new UsageMeteringMiddleware(ctx =>
        {
            observedScope = UsageRequestScope.Current;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, aggregator);

        observedScope.ShouldBeNull();
        aggregator.Swap().Series.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restores_original_body_feature()
    {
        var aggregator = new UsageAggregator();
        using var original = new TestResponseBodyFeature();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/query";
        context.Features.Set<IHttpResponseBodyFeature>(original);
        context.Items[SecurityPrincipalContext.ItemKey] = AuthenticatedContext();

        var okMiddleware = new UsageMeteringMiddleware(ctx =>
        {
            ctx.Features.Get<IHttpResponseBodyFeature>().ShouldNotBeSameAs(original);
            return Task.CompletedTask;
        });

        await okMiddleware.InvokeAsync(context, aggregator);
        context.Features.Get<IHttpResponseBodyFeature>().ShouldBeSameAs(original);
        UsageRequestScope.Current.ShouldBeNull();

        var throwingMiddleware = new UsageMeteringMiddleware(_ => throw new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(() => throwingMiddleware.InvokeAsync(context, aggregator));
        context.Features.Get<IHttpResponseBodyFeature>().ShouldBeSameAs(original);
        UsageRequestScope.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Streaming_flush_semantics_unchanged()
    {
        var aggregator = new UsageAggregator();
        using var inner = new TestResponseBodyFeature();
        using var original = new StreamingBodyFeature(inner);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tables/stream";
        context.Features.Set<IHttpResponseBodyFeature>(original);
        context.Items[SecurityPrincipalContext.ItemKey] = AuthenticatedContext();

        var chunk1 = Encoding.UTF8.GetBytes("{\"chunk\":1}\n");
        var chunk2 = Encoding.UTF8.GetBytes("{\"chunk\":2}\n");

        var middleware = new UsageMeteringMiddleware(async ctx =>
        {
            await ctx.Response.Body.WriteAsync(chunk1);
            await ctx.Response.Body.FlushAsync();
            // Chunk 1 must already have reached the underlying stream on FlushAsync, without waiting for request end.
            inner.FlushCalls.ShouldBe(1);
            inner.FlushedBytesOnFirstFlush.ShouldBe(chunk1.Length);

            await ctx.Response.Body.WriteAsync(chunk2);
            await ctx.Response.Body.FlushAsync();
        });

        await middleware.InvokeAsync(context, aggregator);

        var entry = aggregator.Swap().Series.ShouldHaveSingleItem();
        entry.Value.Bytes.ShouldBe(chunk1.Length + chunk2.Length);
    }
}
