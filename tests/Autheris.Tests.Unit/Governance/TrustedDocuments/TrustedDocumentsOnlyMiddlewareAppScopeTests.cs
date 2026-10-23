namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP3: app-scoped path of the trusted document middleware (SEC-281-1..4, -5, -7).</summary>
[Trait("Category", "Security")]
public sealed class TrustedDocumentsOnlyMiddlewareAppScopeTests : IAsyncDisposable
{
    private const string Doc = "query Q { hello }";
    private const string DocVariant = "query   Q {\n  hello\n}";
    private const string Origin = "https://app.example.com";

    private readonly ITrustedDocumentRepository _repo = Substitute.For<ITrustedDocumentRepository>();
    private readonly CapturingLoggerProvider _logs = new();
    private long _generation = 1;
    private IReadOnlyList<TrustedDocumentVersionRecord> _versions;
    private ServiceProvider? _sp;
    private IRequestExecutor? _executor;

    public TrustedDocumentsOnlyMiddlewareAppScopeTests()
    {
        _versions =
        [
            new("shop", "1", [TrustedDocumentHash.ComputeExact(Doc)], [Origin]),
            new("shop", "2", [TrustedDocumentHash.ComputeExact("query Q2 { hello }")], [Origin]),
            new("other", "1", [TrustedDocumentHash.ComputeExact("query Q3 { hello }")], [Origin])
        ];
        _repo.GetTrustedDocumentGenerationAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_generation));
        _repo.LoadTrustedDocumentSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new TrustedDocumentStoreSnapshot(_generation, _versions)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
        {
            await _sp.DisposeAsync();
        }
    }

    private async Task<IRequestExecutor> ExecutorAsync(bool enabled = true, string[]? globalDocuments = null)
    {
        if (_executor is not null)
        {
            return _executor;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(Options.Create(new GatewayOptions
        {
            GraphQL = new GraphQLOptions { PersistedQueriesOnly = true, AppScopedTrustedDocuments = new AppScopedTrustedDocumentsOptions { Enabled = enabled } }
        }));
        services.AddSingleton<ITrustedDocumentAppRegistry>(new TrustedDocumentAppRegistry(_repo, null, TimeProvider.System));
        services.AddSingleton(new TrustedDocumentStore(globalDocuments ?? []));
        var builder = services.AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("hello").Type<HotChocolate.Types.StringType>().Resolve(_ => new ValueTask<object?>("world")))
            .UseExceptions()
            .UseTimeout()
            .UseDocumentCache()
            .UseRequest<TrustedDocumentsOnlyMiddleware>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseOperationCache()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution();
        _sp = services.BuildServiceProvider();
        _executor = await _sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        return _executor;
    }

    private static DefaultHttpContext Http(string? appHeader, params string[] origins)
    {
        var ctx = new DefaultHttpContext();
        if (appHeader is not null)
        {
            ctx.Request.Headers["X-Autheris-App"] = appHeader;
        }

        if (origins.Length > 0)
        {
            ctx.Request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(origins);
        }

        return ctx;
    }

    private async Task<OperationResult> RunAsync(string document, HttpContext? http, Action<OperationRequestBuilder>? configure = null)
    {
        var executor = await ExecutorAsync();
        var builder = OperationRequestBuilder.New().SetDocument(document);
        if (http is not null)
        {
            builder.SetGlobalState("HttpContext", http);
        }

        configure?.Invoke(builder);
        var result = await executor.ExecuteAsync(builder.Build());
        return result.ShouldBeOfType<OperationResult>();
    }

    private static bool IsRejected(OperationResult r) =>
        r.Errors is { Count: 1 } e && e[0].Code == TrustedDocumentsOnlyMiddleware.RejectionCode;

    private static bool IsOk(OperationResult r) => r.Errors is not { Count: > 0 } && r.Data.HasValue;

    [Fact]
    public async Task Registered_A1_Runs_For_A1()
    {
        IsOk(await RunAsync(Doc, Http("shop/1", Origin))).ShouldBeTrue();
    }

    [Fact]
    public async Task Rejected_For_A2_And_B1_And_Retired()
    {
        IsRejected(await RunAsync(Doc, Http("shop/2", Origin))).ShouldBeTrue();
        IsRejected(await RunAsync(Doc, Http("other/1", Origin))).ShouldBeTrue();
        IsRejected(await RunAsync(Doc, Http("shop/9", Origin))).ShouldBeTrue();
    }

    [Fact]
    public async Task NoHeader_UsesGlobalStore()
    {
        IsRejected(await RunAsync(Doc, Http(null, Origin))).ShouldBeTrue();
        _executor = null;
        await DisposeAsync();
        _sp = null;
        var executor = await ExecutorAsync(globalDocuments: [Doc]);
        var result = (OperationResult)await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(Doc).SetGlobalState("HttpContext", Http(null)).Build());
        IsOk(result).ShouldBeTrue();
    }

    [Fact]
    public async Task Disabled_HeaderIgnored_GlobalPathUnchanged()
    {
        _executor = await ExecutorAsync(enabled: false, globalDocuments: [Doc]);
        IsOk(await RunAsync(Doc, Http("garbage header", "https://evil.example"))).ShouldBeTrue();
        IsRejected(await RunAsync("query Z { hello }", Http("shop/1", Origin))).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("shop")]
    [InlineData("shop/1/2")]
    [InlineData("Shop/1")]
    [InlineData("shop/")]
    [InlineData("shop/1\n")]
    public async Task AppHeader_Present_NoGlobalFallback_AllFailureModes(string header)
    {
        // The document is trusted globally and for shop/1; a present but unusable header still never falls back (SEC-281-2).
        _executor = await ExecutorAsync(globalDocuments: [Doc]);
        IsRejected(await RunAsync(Doc, Http(header, Origin))).ShouldBeTrue();
        IsRejected(await RunAsync(Doc, Http("shop/9", Origin))).ShouldBeTrue();
        IsRejected(await RunAsync(Doc, Http("shop/1"))).ShouldBeTrue();
        IsRejected(await RunAsync(Doc, Http("shop/1", "https://evil.example"))).ShouldBeTrue();
    }

    [Fact]
    public async Task Header_Oversized_MultiValue_Malformed_Rejected_Before_Regex()
    {
        _executor = await ExecutorAsync(globalDocuments: [Doc]);
        IsRejected(await RunAsync(Doc, Http(new string('a', 5000) + "/1", Origin))).ShouldBeTrue();
        var multi = Http(null, Origin);
        multi.Request.Headers["X-Autheris-App"] = new Microsoft.Extensions.Primitives.StringValues(["shop/1", "shop/1"]);
        IsRejected(await RunAsync(Doc, multi)).ShouldBeTrue();
    }

    public static TheoryData<string[]> BadOrigins => new()
    {
        Array.Empty<string>(),
        new[] { "null" },
        new[] { "https://app.example.com", "https://app.example.com" },
        new[] { "https://evil.example.com" },
        new[] { "https://app.example.com/path" }
    };

    [Theory]
    [MemberData(nameof(BadOrigins))]
    public async Task Origin_Missing_Null_Duplicate_Mismatch_Rejected(string[] origins)
    {
        IsRejected(await RunAsync(Doc, Http("shop/1", origins))).ShouldBeTrue();
    }

    [Fact]
    public async Task Origin_CaseAndDefaultPortNormalized_Accepted()
    {
        IsOk(await RunAsync(Doc, Http("shop/1", "HTTPS://APP.Example.com:443"))).ShouldBeTrue();
    }

    [Fact]
    public async Task WebSocket_Upgrade_OriginEnforced()
    {
        var ws = Http("shop/1", "https://evil.example.com");
        ws.Request.Headers.Connection = "Upgrade";
        ws.Request.Headers.Upgrade = "websocket";
        IsRejected(await RunAsync(Doc, ws)).ShouldBeTrue();
        var ok = Http("shop/1", Origin);
        ok.Request.Headers.Connection = "Upgrade";
        ok.Request.Headers.Upgrade = "websocket";
        IsOk(await RunAsync(Doc, ok)).ShouldBeTrue();
    }

    [Fact]
    public async Task NormalizedVariant_Rejected_ExactTextOnly()
    {
        IsRejected(await RunAsync(DocVariant, Http("shop/1", Origin))).ShouldBeTrue();
    }

    [Fact]
    public async Task AppPath_DocumentCache_HitForOtherText_DoesNotBypass()
    {
        IsOk(await RunAsync(Doc, Http("shop/1", Origin))).ShouldBeTrue();   // primes the document cache
        IsRejected(await RunAsync(DocVariant, Http("shop/1", Origin))).ShouldBeTrue();
        IsRejected(await RunAsync(DocVariant, Http("shop/1", Origin), b => b.SetDocumentHash(default))).ShouldBeTrue();
    }

    [Fact]
    public async Task AppPath_Rejects_Request_With_DocumentId_And_Text()
    {
        IsRejected(await RunAsync(Doc, Http("shop/1", Origin), b => b.SetDocumentId(new HotChocolate.Language.OperationDocumentId("abc")))).ShouldBeTrue();
    }

    [Fact]
    public async Task AppPath_Rejects_IdOnly_Request()
    {
        var executor = await ExecutorAsync();
        var request = OperationRequest.FromId("abc").WithServices(_sp!);
        var http = Http("shop/1", Origin);
        var result = (OperationResult)await executor.ExecuteAsync(
            OperationRequestBuilder.From(request).SetGlobalState("HttpContext", http).Build());
        IsOk(result).ShouldBeFalse();
        result.Errors.ShouldNotBeNull();
        result.Errors![0].Code.ShouldBe(TrustedDocumentsOnlyMiddleware.RejectionCode);
    }

    [Fact]
    public async Task AppPath_Rejects_ApqExtension()
    {
        IsRejected(await RunAsync(Doc, Http("shop/1", Origin),
            b => b.SetExtensions(JsonDocument.Parse("{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"abc\"}}")))).ShouldBeTrue();
    }

    [Fact]
    public async Task AppPath_Rejects_PreParsedDocument_WithoutSource()
    {
        IsRejected(await RunAsync(Doc, Http("shop/1", Origin), b => b.SetDocument(HotChocolate.Language.Utf8GraphQLParser.Parse(Doc)))).ShouldBeTrue();
    }

    [Fact]
    public async Task AppPath_Batch_EachItemChecked()
    {
        var executor = await ExecutorAsync();
        var http = Http("shop/1", Origin);
        var trusted = OperationRequestBuilder.New().SetDocument(Doc).SetGlobalState("HttpContext", http).Build();
        var untrusted = OperationRequestBuilder.New().SetDocument("query Evil { hello }").SetGlobalState("HttpContext", http).Build();
        var stream = (ResponseStream)await executor.ExecuteBatchAsync(new OperationRequestBatch([trusted, untrusted]));
        var results = new List<OperationResult>();
        await foreach (var item in stream.ReadResultsAsync())
        {
            results.Add((OperationResult)item);
        }

        results.Count(IsOk).ShouldBe(1);
        results.Count(IsRejected).ShouldBe(1);
    }

    [Fact]
    public async Task AppPath_OperationName_SelectsWithinTrustedDocument()
    {
        _versions = [new("shop", "1", [TrustedDocumentHash.ComputeExact("query A { hello } query B { hello }")], [Origin])];
        IsOk(await RunAsync("query A { hello } query B { hello }", Http("shop/1", Origin), b => b.SetOperationName("B"))).ShouldBeTrue();
    }

    [Fact]
    public async Task AllRejections_ByteIdentical_Response()
    {
        var responses = new List<string>
        {
            (await RunAsync(Doc, Http("bad", Origin))).ToJson(),
            (await RunAsync(Doc, Http("shop/1"))).ToJson(),
            (await RunAsync(Doc, Http("shop/9", Origin))).ToJson(),
            (await RunAsync(DocVariant, Http("shop/1", Origin))).ToJson(),
            (await RunAsync(Doc, Http("shop/1", Origin), b => b.SetDocumentId(new HotChocolate.Language.OperationDocumentId("x")))).ToJson()
        };
        _executor = await ExecutorAsync(globalDocuments: []);
        responses.Add((await RunAsync("query Zzz { hello }", Http(null))).ToJson());   // global path rejection has the same shape
        responses.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Rejection_Log_Contains_No_DocumentText_Or_RawHeader()
    {
        await RunAsync(DocVariant, Http("shop/1", Origin));
        await RunAsync(Doc, Http("zzz-raw-header-value\u0001", Origin));
        var text = string.Join("\n", _logs.Messages);
        text.ShouldContain("NotTrusted");
        text.ShouldNotContain("hello");
        text.ShouldNotContain("zzz-raw-header-value");
    }

    [Fact]
    public async Task Registry_RepositoryThrows_Rejects_And_DoesNotServeStale()
    {
        IsOk(await RunAsync(Doc, Http("shop/1", Origin))).ShouldBeTrue();
        _repo.GetTrustedDocumentGenerationAsync(Arg.Any<CancellationToken>()).Returns<Task<long>>(_ => throw new InvalidOperationException("db down"));
        var registry = _sp!.GetRequiredService<ITrustedDocumentAppRegistry>();
        registry.Invalidate();
        IsRejected(await RunAsync(Doc, Http("shop/1", Origin))).ShouldBeTrue();
    }

    [Fact]
    public async Task CacheRefresh_NewSetAcceptedAfterInvalidate()
    {
        IsRejected(await RunAsync("query New { hello }", Http("shop/1", Origin))).ShouldBeTrue();
        _generation = 2;
        _versions = [new("shop", "1", [TrustedDocumentHash.ComputeExact("query New { hello }")], [Origin])];
        _sp!.GetRequiredService<ITrustedDocumentAppRegistry>().Invalidate();
        IsOk(await RunAsync("query New { hello }", Http("shop/1", Origin))).ShouldBeTrue();
        IsRejected(await RunAsync(Doc, Http("shop/1", Origin))).ShouldBeTrue();   // retire effective locally immediately
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Messages);

        public void Dispose()
        {
        }

        private sealed class Capture(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add(formatter(state, exception));
                }
            }
        }
    }
}
