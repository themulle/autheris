namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-20 (SEC-17, ST-31): an upstream body larger than the cap aborts while it is read, before the executor
/// buffers it; the gateway reads at most the cap plus one buffer.
/// </summary>
public sealed class ResponseSizeLimitingHandlerTests
{
    private const int Cap = 10_000;
    private const int Buffer = 4_096;

    /// <summary>Endless, non-seekable body that counts every byte handed out.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Fill(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fill(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Fill(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, Buffer);
            buffer[..n].Fill((byte)'x');
            BytesServed += n;
            return n;
        }
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private static HttpClient Client(Func<HttpResponseMessage> response, long cap = Cap) =>
        new(new ResponseSizeLimitingHandler(cap) { InnerHandler = new StubHandler(response) });

    [Fact]
    public async Task UnknownLengthBodyAboveTheCap_AbortsAfterAtMostCapPlusOneBuffer()
    {
        var endless = new EndlessStream();
        using var client = Client(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });

        using var response = await client.GetAsync("https://api.example.internal/x", HttpCompletionOption.ResponseHeadersRead);
        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            await body.CopyToAsync(Stream.Null);
        });

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
        endless.BytesServed.ShouldBeLessThanOrEqualTo(Cap + Buffer);
    }

    [Fact]
    public async Task BufferedReadOfAnOversizedBody_Aborts()
    {
        var endless = new EndlessStream();
        using var client = Client(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => client.GetStringAsync("https://api.example.internal/x"));

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
        endless.BytesServed.ShouldBeLessThanOrEqualTo(Cap + Buffer);
    }

    [Fact]
    public async Task DeclaredContentLengthAboveTheCap_AbortsBeforeReadingAnything()
    {
        var endless = new EndlessStream();
        using var client = Client(() =>
        {
            var content = new StreamContent(endless);
            content.Headers.ContentLength = Cap + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => client.GetAsync("https://api.example.internal/x"));

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
        endless.BytesServed.ShouldBe(0);
    }

    [Fact]
    public async Task BodyWithinTheCap_IsReturnedUnchanged_WithItsHeaders()
    {
        var payload = new string('a', Cap);
        using var client = Client(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });

        using var response = await client.GetAsync("https://api.example.internal/x");

        (await response.Content.ReadAsStringAsync()).ShouldBe(payload);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public void NonPositiveCap_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ResponseSizeLimitingHandler(0));
    }

    [Fact]
    public async Task ExecutorOverTheLimitingHandler_FailsTheQuery_InsteadOfBufferingTheBody()
    {
        var endless = new EndlessStream();
        using var client = Client(() =>
        {
            var content = new StreamContent(endless);
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var executor = new DeclarativeHttpDataSourceExecutor(
            factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, environment: env);
        var metadata = FederatedVfPipelineHarness.HttpTable("crm_api", "public", "customers", "id");
        var context = new Autheris.Application.Interfaces.DataSourceExecutionContext(
            SourceName: "crm_api",
            Metadata: metadata,
            Principal: new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test")),
            AccessDecision: Autheris.Domain.Interfaces.TableAccessDecision.Allowed(
                metadata.Identifier, new System.Collections.Generic.Dictionary<string, Autheris.Domain.Interfaces.ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true),
            Arguments: new System.Collections.Generic.Dictionary<string, object?>(),
            RequestedFields: ["id"]);

        var act = () => executor.ExecuteAsync(context);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(act);
        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
        endless.BytesServed.ShouldBeLessThanOrEqualTo(Cap + Buffer);
    }
}
