namespace Autheris.Tests.Unit.Lineage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lineage;
using Autheris.Modules.Lineage.Domain;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenLineageClientTests
{
    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public List<CapturedRequest> Requests { get; } = [];

        public CapturingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        {
            _responder = responder ?? (_ => new HttpResponseMessage(HttpStatusCode.Created));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri, request.Headers.Authorization));
            return Task.FromResult(_responder(request));
        }
    }

    private sealed record CapturedRequest(Uri? RequestUri, AuthenticationHeaderValue? Authorization);

    private sealed class StubGraphStore : ILineageGraphStore
    {
        private readonly IReadOnlyList<LineageNode> _nodes;

        public StubGraphStore(params LineageNode[] nodes) => _nodes = nodes;

        public LineageNode? GetNode(string nodeId) => _nodes.FirstOrDefault(n => n.Id == nodeId);

        public bool ContainsNode(string nodeId) => _nodes.Any(n => n.Id == nodeId);

        public void UpdateGraph(IEnumerable<LineageNode> nodes)
        {
        }

        public IReadOnlyCollection<LineageNode> GetAllNodes() => _nodes;

        public int Count => _nodes.Count;
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static OpenLineageRunEvent SampleEvent() => new(
        EventType: "COMPLETE",
        EventTime: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
        Producer: "https://github.com/themulle/gql/autheris",
        SchemaUrl: "https://openlineage.io/spec/1-0-2/OpenLineage.json",
        Job: new { @namespace = "tenant-a", name = "sync-job" },
        Inputs: [new OpenLineageDataset("finance", "dbo.invoices")],
        Outputs: [new OpenLineageDataset("bi", "dashboard.sales")]);

    private static OpenLineageClient CreateClient(
        string? endpoint,
        string apiKey = "",
        IHostEnvironment? environment = null,
        CapturingHttpHandler? handler = null,
        RecordingLogger<OpenLineageClient>? logger = null,
        ILineageGraphStore? graphStore = null,
        IKeyVaultSecretProvider? secretProvider = null)
    {
        var options = Options.Create(new LineageModuleOptions
        {
            OpenLineageEndpoint = endpoint!,
            OpenLineageApiKey = apiKey
        });
        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" }
        });

        return new OpenLineageClient(
            new HttpClient(handler ?? new CapturingHttpHandler()),
            graphStore ?? new StubGraphStore(new LineageNode("finance.dbo.invoices", "dbo.invoices", LineageNodeType.Table, [], TenantId: "tenant-a")),
            options,
            logger ?? new RecordingLogger<OpenLineageClient>(),
            gatewayOptions,
            environment,
            secretProvider: secretProvider);
    }

    [Fact]
    public async Task Bearer_key_not_sent_over_http()
    {
        var devHandler = new CapturingHttpHandler();
        var devClient = CreateClient(
            "http://openlineage.internal/api/v1/lineage",
            apiKey: "super-secret-bearer-token",
            environment: new StubHostEnvironment(Environments.Development),
            handler: devHandler);

        var devResult = await devClient.PushLineageEventAsync(SampleEvent());
        devResult.ShouldBeTrue();
        devHandler.Requests.Count.ShouldBe(1);
        devHandler.Requests[0].Authorization.ShouldBeNull();

        var prodHandler = new CapturingHttpHandler();
        var prodClient = CreateClient(
            "http://openlineage.internal/api/v1/lineage",
            apiKey: "super-secret-bearer-token",
            environment: new StubHostEnvironment(Environments.Production),
            handler: prodHandler);

        var prodResult = await prodClient.PushLineageEventAsync(SampleEvent());
        prodResult.ShouldBeFalse();
        prodHandler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bearer_key_allowed_over_loopback_in_development()
    {
        var handler = new CapturingHttpHandler();
        var client = CreateClient(
            "http://localhost:5000/api/v1/lineage",
            apiKey: "local-dev-token",
            environment: new StubHostEnvironment(Environments.Development),
            handler: handler);

        var result = await client.PushLineageEventAsync(SampleEvent());

        result.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Authorization.ShouldNotBeNull();
        handler.Requests[0].Authorization!.Scheme.ShouldBe("Bearer");
        handler.Requests[0].Authorization!.Parameter.ShouldBe("local-dev-token");
    }

    [Theory]
    [InlineData("https://alice:top-secret-pass@openlineage.example.com/api/v1/lineage", true)]
    [InlineData("https://alice@openlineage.example.com/api/v1/lineage", false)]
    [InlineData("https://@openlineage.example.com/api/v1/lineage", false)]
    public async Task Endpoint_with_userinfo_rejected(string endpoint, bool isDev)
    {
        var handler = new CapturingHttpHandler();
        var logger = new RecordingLogger<OpenLineageClient>();
        var env = new StubHostEnvironment(isDev ? Environments.Development : Environments.Production);
        var client = CreateClient(endpoint, apiKey: "token", environment: env, handler: handler, logger: logger);

        var pushEvent = await client.PushLineageEventAsync(SampleEvent());
        var pushGraph = await client.PushLineageGraphAsync(new TenantId("tenant-a"));

        pushEvent.ShouldBeFalse();
        pushGraph.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);

        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain("alice");
            entry.Message.ShouldNotContain("top-secret-pass");
            entry.Message.ShouldNotContain("@");
        }
    }

    [Fact]
    public async Task Log_contains_no_query_string()
    {
        const string secretQueryEndpoint = "https://openlineage.example.com/api/v1/lineage?token=query-secret-123&env=prod";

        // 1. Success response (201): logs host and status code only, no query string
        var okLogger = new RecordingLogger<OpenLineageClient>();
        var okHandler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("sensitive-ok-body", Encoding.UTF8, "text/plain")
        });
        var okClient = CreateClient(secretQueryEndpoint, environment: new StubHostEnvironment(Environments.Production), handler: okHandler, logger: okLogger);
        (await okClient.PushLineageEventAsync(SampleEvent())).ShouldBeTrue();

        okLogger.Entries.ShouldContain(e => e.Message.Contains("openlineage.example.com") && e.Message.Contains("201"));

        // 2. Error HTTP response (502): logs status code and host only, no body or query string
        var failLogger = new RecordingLogger<OpenLineageClient>();
        var failHandler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("sensitive-backend-error-body", Encoding.UTF8, "text/plain")
        });
        var failClient = CreateClient(secretQueryEndpoint, environment: new StubHostEnvironment(Environments.Production), handler: failHandler, logger: failLogger);
        (await failClient.PushLineageEventAsync(SampleEvent())).ShouldBeFalse();

        failLogger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("openlineage.example.com") && e.Message.Contains("502"));

        // 3. Transport exception: no query string or userinfo leaked in log or exception object
        var exLogger = new RecordingLogger<OpenLineageClient>();
        var exHandler = new CapturingHttpHandler(_ => throw new HttpRequestException($"Connection failed for {secretQueryEndpoint}"));
        var exClient = CreateClient(secretQueryEndpoint, environment: new StubHostEnvironment(Environments.Production), handler: exHandler, logger: exLogger);
        (await exClient.PushLineageEventAsync(SampleEvent())).ShouldBeFalse();

        exLogger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("openlineage.example.com"));

        foreach (var entry in okLogger.Entries.Concat(failLogger.Entries).Concat(exLogger.Entries))
        {
            entry.Message.ShouldNotContain("query-secret-123");
            entry.Message.ShouldNotContain("token=");
            entry.Message.ShouldNotContain("?");
            entry.Message.ShouldNotContain("sensitive-ok-body");
            entry.Message.ShouldNotContain("sensitive-backend-error-body");
            entry.Exception.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Http_endpoint_refused_outside_development(string? environmentName)
    {
        var handler = new CapturingHttpHandler();
        var logger = new RecordingLogger<OpenLineageClient>();
        var env = environmentName is null ? null : new StubHostEnvironment(environmentName);
        var client = CreateClient("http://openlineage.example.com/api/v1/lineage", apiKey: "secret", environment: env, handler: handler, logger: logger);

        (await client.PushLineageEventAsync(SampleEvent())).ShouldBeFalse();
        (await client.PushLineageGraphAsync(new TenantId("tenant-a"))).ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
    }

    [Fact]
    public async Task Unset_endpoint_disables_push()
    {
        new LineageModuleOptions().OpenLineageEndpoint.ShouldBeEmpty();

        var handler = new CapturingHttpHandler();
        var logger = new RecordingLogger<OpenLineageClient>();
        var client = CreateClient(new LineageModuleOptions().OpenLineageEndpoint, environment: new StubHostEnvironment(Environments.Production), handler: handler, logger: logger);

        (await client.PushLineageEventAsync(SampleEvent())).ShouldBeFalse();
        (await client.PushLineageGraphAsync(new TenantId("tenant-a"))).ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData("not-a-valid-uri")]
    [InlineData("ftp://openlineage.example.com/api/v1/lineage")]
    [InlineData("https://localhost:5000/api/v1/lineage")]
    [InlineData("https://169.254.169.254/api/v1/lineage")]
    [InlineData("https://10.1.2.3/api/v1/lineage")]
    public async Task Validation_failure_disables_client_with_one_startup_error(string invalidEndpoint)
    {
        var handler = new CapturingHttpHandler();
        var logger = new RecordingLogger<OpenLineageClient>();
        var client = CreateClient(invalidEndpoint, apiKey: "secret", environment: new StubHostEnvironment(Environments.Production), handler: handler, logger: logger);

        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);

        for (int i = 0; i < 3; i++)
        {
            (await client.PushLineageEventAsync(SampleEvent())).ShouldBeFalse();
            (await client.PushLineageGraphAsync(new TenantId("tenant-a"))).ShouldBeFalse();
        }

        handler.Requests.ShouldBeEmpty();
        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
    }

    [Fact]
    public async Task Https_endpoint_outside_development_sends_bearer_key()
    {
        var handler = new CapturingHttpHandler();
        var secretProvider = new StubSecretProvider(reference =>
            reference == "kv://openlineage-prod-token"
                ? Encoding.UTF8.GetBytes("prod-token")
                : throw new InvalidOperationException($"Unknown secret '{reference}'"));
        var client = CreateClient(
            "https://openlineage.example.com/api/v1/lineage",
            apiKey: "kv://openlineage-prod-token",
            environment: new StubHostEnvironment(Environments.Production),
            handler: handler,
            secretProvider: secretProvider);

        var result = await client.PushLineageEventAsync(SampleEvent());

        result.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Authorization.ShouldNotBeNull();
        handler.Requests[0].Authorization!.Scheme.ShouldBe("Bearer");
        handler.Requests[0].Authorization!.Parameter.ShouldBe("prod-token");
    }

    [Fact]
    public async Task PlaintextApiKey_InProduction_FailsClosedWithoutSending()
    {
        var handler = new CapturingHttpHandler();
        var client = CreateClient(
            "https://openlineage.example.com/api/v1/lineage",
            apiKey: "plaintext-prod-token",
            environment: new StubHostEnvironment(Environments.Production),
            handler: handler);

        await Should.ThrowAsync<System.Security.SecurityException>(() => client.PushLineageEventAsync(SampleEvent()));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnresolvableSecretReference_InProduction_FailsClosedWithoutSending()
    {
        var handler = new CapturingHttpHandler();
        var logger = new RecordingLogger<OpenLineageClient>();
        const string secretRef = "kv://missing-openlineage-secret";
        var secretProvider = new StubSecretProvider(_ =>
            throw new InvalidOperationException($"Failed to read secret 'raw-secret-leak-sentinel' at {secretRef}"));
        var client = CreateClient(
            "https://openlineage.example.com/api/v1/lineage",
            apiKey: secretRef,
            environment: new StubHostEnvironment(Environments.Production),
            handler: handler,
            logger: logger,
            secretProvider: secretProvider);

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(() => client.PushLineageEventAsync(SampleEvent()));

        ex.Message.ShouldNotContain("raw-secret-leak-sentinel");
        handler.Requests.ShouldBeEmpty();
        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("raw-secret-leak-sentinel"));
    }

    private sealed class StubSecretProvider(Func<string, byte[]> resolver) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => resolver(secretRef);
    }
}
