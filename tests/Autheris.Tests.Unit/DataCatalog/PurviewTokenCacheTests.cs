namespace Autheris.Tests.Unit.DataCatalog;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class PurviewTokenCacheTests
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-26T10:00:00Z"));
    private Action<GatewayOptions, string?>? _listener;

    private PurviewTokenCache NewCache()
    {
        return new PurviewTokenCache(_time, cb =>
        {
            _listener = (_, _) => cb();
            return null;
        });
    }

    private static Func<CancellationToken, Task<(string, TimeSpan)>> Fetch(string token, Action? onCall = null) =>
        _ =>
        {
            onCall?.Invoke();
            return Task.FromResult((token, OneHour));
        };

    [Fact]
    public async Task TwoCredentialSets_DoNotShareTokens()
    {
        using var cache = NewCache();
        var a = PurviewTokenCache.ComputeKey("t1", "c1", "s1");
        var b = PurviewTokenCache.ComputeKey("t2", "c1", "s1");

        (await cache.GetOrFetchAsync(a, Fetch("tok-a"), default)).ShouldBe("tok-a");
        (await cache.GetOrFetchAsync(b, Fetch("tok-b"), default)).ShouldBe("tok-b");
        (await cache.GetOrFetchAsync(b, Fetch("never"), default)).ShouldBe("tok-b");
    }

    [Fact]
    public async Task SecretRotation_Refetches()
    {
        using var cache = NewCache();
        (await cache.GetOrFetchAsync(PurviewTokenCache.ComputeKey("t", "c", "old"), Fetch("t-old"), default)).ShouldBe("t-old");
        (await cache.GetOrFetchAsync(PurviewTokenCache.ComputeKey("t", "c", "new"), Fetch("t-new"), default)).ShouldBe("t-new");
    }

    [Fact]
    public async Task OptionsChange_ClearsCache()
    {
        using var cache = NewCache();
        var key = PurviewTokenCache.ComputeKey("t", "c", "s");
        await cache.GetOrFetchAsync(key, Fetch("one"), default);
        cache.HasCachedToken.ShouldBeTrue();

        _listener.ShouldNotBeNull();
        _listener!(new GatewayOptions(), null);

        cache.HasCachedToken.ShouldBeFalse();
        (await cache.GetOrFetchAsync(key, Fetch("two"), default)).ShouldBe("two");
    }

    [Fact]
    public async Task Expiry_Refetches()
    {
        using var cache = NewCache();
        var key = PurviewTokenCache.ComputeKey("t", "c", "s");
        await cache.GetOrFetchAsync(key, Fetch("one"), default);

        _time.Advance(OneHour - TimeSpan.FromSeconds(61));
        (await cache.GetOrFetchAsync(key, Fetch("never"), default)).ShouldBe("one");

        _time.Advance(TimeSpan.FromSeconds(2)); // inside the 60 s skew window
        (await cache.GetOrFetchAsync(key, Fetch("two"), default)).ShouldBe("two");
    }

    [Fact]
    public async Task ConcurrentCallers_FetchOnce()
    {
        using var cache = NewCache();
        var key = PurviewTokenCache.ComputeKey("t", "c", "s");
        var calls = 0;
        var release = new TaskCompletionSource();

        async Task<(string, TimeSpan)> Slow(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return ("tok", OneHour);
        }

        var tasks = new List<Task<string>>();
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(cache.GetOrFetchAsync(key, Slow, default));
        }

        release.SetResult();
        (await Task.WhenAll(tasks)).ShouldAllBe(t => t == "tok");
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task FetchStartedBeforeOptionsChange_DoesNotPopulateCache()
    {
        using var cache = NewCache();
        var key = PurviewTokenCache.ComputeKey("t", "c", "s");
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        async Task<(string, TimeSpan)> Slow(CancellationToken _)
        {
            inFlight.SetResult();
            await release.Task;
            return ("stale", OneHour);
        }

        var pending = cache.GetOrFetchAsync(key, Slow, default);
        await inFlight.Task;
        _listener!(new GatewayOptions(), null);
        release.SetResult();

        (await pending).ShouldBe("stale");
        cache.HasCachedToken.ShouldBeFalse();
    }

    [Fact]
    public async Task FailedFetch_IsNotCached()
    {
        using var cache = NewCache();
        var key = PurviewTokenCache.ComputeKey("t", "c", "s");
        await cache.GetOrFetchAsync(key, Fetch("good"), default);
        _time.Advance(OneHour);

        await Should.ThrowAsync<HttpRequestException>(() =>
            cache.GetOrFetchAsync(key, _ => throw new HttpRequestException("boom"), default));

        cache.HasCachedToken.ShouldBeTrue(); // expired slot stays unusable ...
        (await cache.GetOrFetchAsync(key, Fetch("fresh"), default)).ShouldBe("fresh"); // ... and is never served as fallback
    }

    [Fact]
    public async Task CancelledCaller_WhileWaitingForGate_ThrowsOperationCanceled()
    {
        using var cache = NewCache();
        var key = PurviewTokenCache.ComputeKey("t", "c", "s");
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = cache.GetOrFetchAsync(key, async _ =>
        {
            inFlight.SetResult();
            await release.Task;
            return ("tok", OneHour);
        }, default);
        await inFlight.Task;

        using var cts = new CancellationTokenSource();
        var second = cache.GetOrFetchAsync(key, Fetch("x"), cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => second);

        release.SetResult();
        await first;
    }

    [Fact]
    public void ComputeKey_IsOpaque_AndDoesNotContainInputs()
    {
        var key = PurviewTokenCache.ComputeKey("tenant-x", "client-y", "SuperSecret!");
        key.ShouldNotContain("SuperSecret");
        key.ShouldNotContain("tenant-x");
        key.Length.ShouldBe(64);
    }

    [Fact]
    public async Task DevMockToken_IsNeverCached()
    {
        var cache = new PurviewTokenCache(_time);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler, new GatewayOptions(), "Development", cache, new RecordingLogger());

        await client.GetTablesAsync(ct: default);

        cache.HasCachedToken.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TokenAndSecret_NeverInLogsOrExceptions()
    {
        const string secret = "S3cr3t-VALUE-xyz";
        var options = new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Purview = new PurviewOptions
                {
                    TenantId = "tenant-1",
                    ClientId = "client-1",
                    ClientSecret = secret,
                    Endpoint = "https://20.50.60.70",
                },
            },
        };
        var logger = new RecordingLogger();
        var cache = new PurviewTokenCache(_time);
        var fingerprint = PurviewTokenCache.ComputeKey("tenant-1", "client-1", secret);

        var failing = NewClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), options, "Development", cache, logger);
        var ex = await Should.ThrowAsync<HttpRequestException>(() => failing.GetTablesAsync(ct: default));

        var ok = NewClient(new StubHandler(req => req.RequestUri!.AbsolutePath.Contains("oauth2", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"TOKEN-ABC\",\"expires_in\":3600}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"entities\":[]}") }), options, "Development", cache, logger);
        await ok.GetTablesAsync(ct: default);

        var all = ex.ToString() + string.Join("\n", logger.Lines);
        all.ShouldNotContain(secret);
        all.ShouldNotContain("TOKEN-ABC");
        all.ShouldNotContain(fingerprint);
        cache.HasCachedToken.ShouldBeTrue();
    }

    private static PurviewDataCatalogClient NewClient(HttpMessageHandler handler, GatewayOptions options, string env, PurviewTokenCache cache, ILogger<PurviewDataCatalogClient> logger)
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(env);
        return new PurviewDataCatalogClient(new HttpClient(handler), Options.Create(options), logger, host, null, cache);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class RecordingLogger : ILogger<PurviewDataCatalogClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }
}
