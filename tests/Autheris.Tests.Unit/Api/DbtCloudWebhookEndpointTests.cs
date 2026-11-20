namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Dbt;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DbtCloudWebhookEndpointTests
{
    private const string Secret = "dbt-cloud-secret-for-endpoint-tests";
    private const string Payload = """{"eventId":"evt_9","eventType":"job_run.completed"}""";

    [Fact]
    public async Task Unsigned_request_returns_401_and_never_reaches_handler()
    {
        var h = Create();

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
        h.Body.WasRead.ShouldBeFalse();
    }

    [Fact]
    public async Task Invalid_signature_never_reaches_handler()
    {
        var h = Create();
        h.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, "wrong-secret");

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("X-Dbt-Signature", false)]
    [InlineData("X-Dbt-Signature", true)]
    [InlineData("X-Hub-Signature-256", true)]
    [InlineData("Authorization", false)]
    public async Task Valid_signature_reaches_handler(string header, bool withPrefix)
    {
        var h = Create();
        var hex = DbtCloudWebhookSignatureTests.Hex(Payload, Secret);
        h.Context.Request.Headers[header] = withPrefix ? "sha256=" + hex : hex;

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status200OK);
        h.Handler.Calls.ShouldBe(1);
        h.Handler.LastPayload.ShouldBe(Payload);
    }

    [Fact]
    public async Task Authorization_header_hmac_is_accepted()
    {
        var h = Create();
        h.Context.Request.Headers.Authorization = DbtCloudWebhookSignatureTests.Hex(Payload, Secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status200OK);
        h.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Authorization_bearer_value_is_not_treated_as_signature()
    {
        var h = Create();
        h.Context.Request.Headers.Authorization = "Bearer " + DbtCloudWebhookSignatureTests.Hex(Payload, Secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
        h.Body.WasRead.ShouldBeFalse();
    }

    [Fact]
    public async Task Sha256_prefix_in_authorization_header_rejected()
    {
        var h = Create();
        h.Context.Request.Headers.Authorization = "sha256=" + DbtCloudWebhookSignatureTests.Hex(Payload, Secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("abcdef")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task Non_hex_or_wrong_length_signature_rejected(string value)
    {
        var h = Create();
        h.Context.Request.Headers["X-Dbt-Signature"] = value;

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Body_over_2_mb_returns_413()
    {
        var h = Create(rawBody: new byte[2 * 1024 * 1024 + 1024]);
        h.Context.Request.Headers["X-Dbt-Signature"] = "sha256=abcdef";

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        h.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Signature_bypass_flag_honored_only_when_set()
    {
        var bypassed = Create(bypass: true);
        bypassed.Context.Request.Headers["X-Dbt-Signature"] = "not-a-valid-signature";
        (await InvokeAsync(bypassed)).Status.ShouldBe(StatusCodes.Status200OK);
        bypassed.Handler.Calls.ShouldBe(1);

        var enforced = Create(bypass: false);
        enforced.Context.Request.Headers["X-Dbt-Signature"] = "not-a-valid-signature";
        (await InvokeAsync(enforced)).Status.ShouldBe(StatusCodes.Status401Unauthorized);
        enforced.Handler.Calls.ShouldBe(0);

        var noHeader = Create(bypass: true);
        (await InvokeAsync(noHeader)).Status.ShouldBe(StatusCodes.Status401Unauthorized);
        noHeader.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Missing_and_invalid_signature_return_identical_401_body()
    {
        var missing = Create();
        var invalid = Create();
        invalid.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, "wrong-secret");

        var m = await InvokeAsync(missing);
        var i = await InvokeAsync(invalid);

        m.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        i.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        i.Body.ShouldBe(m.Body);
        JsonDocument.Parse(m.Body).RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        m.Body.ShouldContain("Invalid or missing HMAC signature");
    }

    [Fact]
    public async Task Handler_not_resolved_before_signature_verified()
    {
        var unsigned = Create();
        await InvokeAsync(unsigned);
        unsigned.HandlerResolutions().ShouldBe(0);

        var invalid = Create();
        invalid.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, "wrong-secret");
        await InvokeAsync(invalid);
        invalid.HandlerResolutions().ShouldBe(0);

        var valid = Create();
        valid.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, Secret);
        await InvokeAsync(valid);
        valid.HandlerResolutions().ShouldBe(1);
    }

    [Fact]
    public async Task Absent_module_returns_404_before_reading_headers_body_or_options()
    {
        var h = Create(register: false);
        h.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, Secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status404NotFound);
        h.Body.WasRead.ShouldBeFalse();
        h.Handler.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_secret_rejects_all_webhooks(string secret)
    {
        var h = Create(secret: secret);
        h.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, secret);

        var (status, _) = await InvokeAsync(h);

        status.ShouldBe(StatusCodes.Status401Unauthorized);
        h.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Environment_secret_fallback_is_ignored()
    {
        const string envSecret = "secret-from-environment-only";
        var previous = Environment.GetEnvironmentVariable("DBT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("DBT_WEBHOOK_SECRET", envSecret);
        try
        {
            var h = Create(secret: string.Empty);
            h.Context.Request.Headers["X-Dbt-Signature"] = DbtCloudWebhookSignatureTests.Hex(Payload, envSecret);

            var (status, _) = await InvokeAsync(h);

            status.ShouldBe(StatusCodes.Status401Unauthorized);
            h.Handler.Calls.ShouldBe(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBT_WEBHOOK_SECRET", previous);
        }
    }

    [Fact]
    public async Task Secret_and_signature_never_logged()
    {
        var wrongSignature = DbtCloudWebhookSignatureTests.Hex(Payload, "wrong-secret");
        var rejected = Create();
        rejected.Context.Request.Headers["X-Dbt-Signature"] = wrongSignature;
        rejected.Context.Request.Headers.Authorization = wrongSignature;
        var (_, rejectedBody) = await InvokeAsync(rejected);

        var bypassed = Create(bypass: true);
        var bypassSignature = DbtCloudWebhookSignatureTests.Hex(Payload, "another-wrong-secret");
        bypassed.Context.Request.Headers["X-Hub-Signature-256"] = "sha256=" + bypassSignature;
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

    private sealed class RecordingHandler : IDbtCloudWebhookHandler
    {
        public int Calls { get; private set; }
        public string? LastPayload { get; private set; }

        public Task<DbtWebhookProcessingResult> HandleVerifiedAsync(string payload, CancellationToken ct)
        {
            Calls++;
            LastPayload = payload;
            return Task.FromResult(new DbtWebhookProcessingResult(true, "Processed", "job_run.completed", 1L));
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
        byte[]? rawBody = null)
    {
        var handler = new RecordingHandler();
        var logs = new CapturingLoggerFactory();
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        if (register)
        {
            services.AddScoped<IDbtCloudWebhookHandler>(_ =>
            {
                resolutions++;
                return handler;
            });
            services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions
            {
                Dbt = new DbtOptions { WebhookSecret = secret ?? string.Empty },
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
        var result = await WebhookEndpoints.HandleDbtCloudWebhookAsync(h.Context);
        await result.ExecuteAsync(h.Context);
        h.Context.Response.Body.Position = 0;
        return (h.Context.Response.StatusCode, await new StreamReader(h.Context.Response.Body).ReadToEndAsync());
    }
}
