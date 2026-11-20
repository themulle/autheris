namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class OpenMetadataWebhookEndpointTests
{
    private const string Secret = "om-webhook-secret-for-endpoint-tests";
    private const string Payload = """{"id":"11111111-1111-1111-1111-111111111111","timestamp":1700000000000,"eventType":"entityUpdated","entityType":"table"}""";

    private static string Hex(string payload, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    [Fact]
    [Trait("Category", "Security")]
    public async Task Handler_absent_returns_404_before_header_or_body_read()
    {
        var h = Create(register: false, rawBody: new byte[2 * 1024 * 1024 + 4096]);
        h.Context.Request.Headers["X-OpenMetadata-Signature"] = Hex(Payload, Secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status404NotFound);
        h.Body.WasRead.ShouldBeFalse();
        h.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Missing_and_invalid_signature_return_identical_401_body()
    {
        var missing = Create();
        var invalid = Create();
        invalid.Context.Request.Headers["X-OpenMetadata-Signature"] = Hex(Payload, "wrong-secret");

        var blank = Create(secret: string.Empty);
        blank.Context.Request.Headers["X-OpenMetadata-Signature"] = Hex(Payload, Secret);

        var m = await InvokeAsync(missing);
        var i = await InvokeAsync(invalid);
        var b = await InvokeAsync(blank);

        m.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        i.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        b.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        i.Body.ShouldBe(m.Body);
        b.Body.ShouldBe(m.Body);
        var doc = JsonDocument.Parse(m.Body).RootElement;
        doc.GetProperty("detail").GetString().ShouldBe("Invalid or missing HMAC signature.");
        doc.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.WebhookSignatureOrFormatInvalid);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Handler_not_resolved_before_signature_verified()
    {
        var unsigned = Create();
        await InvokeAsync(unsigned);
        unsigned.HandlerResolutions().ShouldBe(0);

        var invalid = Create();
        invalid.Context.Request.Headers["X-OpenMetadata-Signature"] = Hex(Payload, "wrong-secret");
        await InvokeAsync(invalid);
        invalid.HandlerResolutions().ShouldBe(0);

        var valid = Create();
        valid.Context.Request.Headers["X-OpenMetadata-Signature"] = "sha256=" + Hex(Payload, Secret);
        var (status, body) = await InvokeAsync(valid);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("Processed");
        valid.HandlerResolutions().ShouldBe(1);
        valid.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Body_over_2_mb_returns_413()
    {
        var h = Create(rawBody: new byte[2 * 1024 * 1024 + 1024]);
        h.Context.Request.Headers["X-OpenMetadata-Signature"] = "sha256=abcdef";

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        h.Handler.Calls.ShouldBe(0);
        h.HandlerResolutions().ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_secret_rejects_all_webhooks(string secret)
    {
        var h = Create(secret: secret);
        h.Context.Request.Headers["X-OpenMetadata-Signature"] = Hex(Payload, secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Signature_bypass_flag_honored_only_when_set()
    {
        var bypassed = Create(bypass: true);
        bypassed.Context.Request.Headers["X-OM-Signature"] = "not-a-valid-signature";
        (await InvokeAsync(bypassed)).Status.ShouldBe(StatusCodes.Status200OK);
        bypassed.Handler.Calls.ShouldBe(1);

        var enforced = Create(bypass: false);
        enforced.Context.Request.Headers["X-OM-Signature"] = "not-a-valid-signature";
        (await InvokeAsync(enforced)).Status.ShouldBe(StatusCodes.Status401Unauthorized);
        enforced.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Secret_and_signature_never_logged()
    {
        var wrongSignature = Hex(Payload, "wrong-secret");
        var rejected = Create();
        rejected.Context.Request.Headers["X-OpenMetadata-Signature"] = wrongSignature;
        var (_, rejectedBody) = await InvokeAsync(rejected);

        var bypassed = Create(bypass: true);
        var bypassSignature = Hex(Payload, "another-wrong-secret");
        bypassed.Context.Request.Headers["X-OM-Signature"] = "sha256=" + bypassSignature;
        var (_, bypassedBody) = await InvokeAsync(bypassed);

        var logged = string.Join('\n', rejected.Logs.Entries.Concat(bypassed.Logs.Entries));
        logged.ShouldNotBeEmpty();
        foreach (var forbidden in new[] { Secret, wrongSignature, bypassSignature, Payload })
        {
            logged.ShouldNotContain(forbidden);
            rejectedBody.ShouldNotContain(forbidden);
            bypassedBody.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Empty_primary_header_falls_back_to_om_signature_header()
    {
        var h = Create();
        h.Context.Request.Headers["X-OpenMetadata-Signature"] = "   ";
        h.Context.Request.Headers["X-OM-Signature"] = Hex(Payload, Secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status200OK);
        h.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Missing_header_returns_401_even_when_bypass_set()
    {
        var h = Create(bypass: true);
        h.Context.Request.Headers["X-OpenMetadata-Signature"] = "   ";

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Body.WasRead.ShouldBeFalse();
        h.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Body_not_read_before_signature_header_present()
    {
        var h = Create();

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Body.WasRead.ShouldBeFalse();
        h.HandlerResolutions().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Route_keeps_anonymous_2mb_limit_and_full_audit()
    {
        using var builder = new TestEndpointRouteBuilder();
        WebhookEndpoints.MapWebhookEndpoints(builder, new GatewayOptions());

        var endpoint = builder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, "/api/webhooks/openmetadata", StringComparison.Ordinal));

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        endpoint.Metadata.GetOrderedMetadata<IRequestSizeLimitMetadata>().LastOrDefault()?.MaxRequestBodySize
            .ShouldBe(2L * 1024 * 1024);

        var audit = endpoint.Metadata.GetMetadata<AuditPolicy>().ShouldNotBeNull();
        audit.Level.ShouldBe(AuditLevel.Full);
        audit.EventType.ShouldBe(AuditEventTypes.AuditConfigChanged);
    }

    [Fact]
    public async Task Processing_failure_returns_400()
    {
        var h = Create(handlerOutcome: OpenMetadataWebhookOutcome.Rejected);
        h.Context.Request.Headers["X-OpenMetadata-Signature"] = Hex(Payload, Secret);

        var (status, body) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("Failed to process webhook.");
        h.Handler.Calls.ShouldBe(1);
    }

    private sealed class RecordingHandler(OpenMetadataWebhookOutcome outcome) : IOpenMetadataWebhookHandler
    {
        public int Calls { get; private set; }
        public string? LastPayload { get; private set; }

        public Task<OpenMetadataWebhookOutcome> HandleVerifiedAsync(string payload, CancellationToken ct)
        {
            Calls++;
            LastPayload = payload;
            return Task.FromResult(outcome);
        }
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool WasRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WasRead = true;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Entries { get; } = [];
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(formatter(state, exception) + exception);
        }
    }

    private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder, IDisposable
    {
        private readonly ServiceProvider _provider;

        public TestEndpointRouteBuilder()
        {
            var services = new ServiceCollection();
            services.AddRouting();
            services.AddLogging();
            services.AddAuthorization();
            _provider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider => _provider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
        public void Dispose() => _provider.Dispose();
    }

    private sealed record Harness(
        DefaultHttpContext Context,
        RecordingHandler Handler,
        TrackingStream Body,
        CapturingLoggerFactory Logs,
        Func<int> HandlerResolutions);

    private static Harness Create(
        string? body = null,
        string? secret = Secret,
        bool bypass = false,
        bool register = true,
        OpenMetadataWebhookOutcome handlerOutcome = OpenMetadataWebhookOutcome.Processed,
        byte[]? rawBody = null)
    {
        var handler = new RecordingHandler(handlerOutcome);
        var logs = new CapturingLoggerFactory();
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        if (register)
        {
            services.AddScoped<IOpenMetadataWebhookHandler>(_ =>
            {
                resolutions++;
                return handler;
            });
            services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions
            {
                OpenMetadata = new OpenMetadataOptions { WebhookSecret = secret ?? string.Empty },
                Insecure = new InsecureGettingStartedOptions { danger_bypass_webhook_signature_validation = bypass },
            }));
        }

        var stream = new TrackingStream(rawBody ?? Encoding.UTF8.GetBytes(body ?? Payload));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider };
        context.Request.Body = stream;
        context.Request.ContentLength = stream.Length;
        context.Response.Body = new MemoryStream();
        return new Harness(context, handler, stream, logs, () => resolutions);
    }

    private static async Task<(int Status, string Body)> InvokeAsync(Harness h)
    {
        var result = await WebhookEndpoints.HandleOpenMetadataWebhookAsync(h.Context);
        await result.ExecuteAsync(h.Context);
        h.Context.Response.Body.Position = 0;
        return (h.Context.Response.StatusCode, await new StreamReader(h.Context.Response.Body).ReadToEndAsync());
    }
}
