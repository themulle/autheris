namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-SEC-115 WP-4 (A-9, C-115-5): the observer counts requests to the switched-off REST data family and logs a bounded,
/// rate-limited warning for authenticated principals only. It never writes a response, never short-circuits and never throws.
/// </summary>
public sealed class DisabledReadPathObserverMiddlewareTests
{
    private sealed class RecordingLogger : ILogger<DisabledReadPathObserverMiddleware>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}|{formatter(state, exception)}");
    }

    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _restData;
        private long _errors;

        public CounterProbe()
        {
            _listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Name == "autheris_disabled_path_requests_total") l.EnableMeasurementEvents(inst);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var path = tags.ToArray().FirstOrDefault(t => t.Key == "path").Value?.ToString();
                if (path == "rest_data") Interlocked.Add(ref _restData, value);
                if (path == "observer_error") Interlocked.Add(ref _errors, value);
            });
            _listener.Start();
        }

        public long RestData => Interlocked.Read(ref _restData);
        public long Errors => Interlocked.Read(ref _errors);
        public void Dispose() => _listener.Dispose();
    }

    private static DefaultHttpContext Ctx(string path, string method = "GET", string? principal = null, string query = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        if (principal is not null)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, principal)], "Test"));
        }

        return ctx;
    }

    private static (DisabledReadPathObserverMiddleware Mw, RecordingLogger Log, FakeTimeProvider Time, Func<int> NextCalls) Build()
    {
        var calls = 0;
        var logger = new RecordingLogger();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
        var mw = new DisabledReadPathObserverMiddleware(_ => { calls++; return Task.CompletedTask; }, logger, time);
        return (mw, logger, time, () => calls);
    }

    [Theory]
    [InlineData("/api/v1/data", true)]
    [InlineData("/api/v1/data/x", true)]
    [InlineData("/API/V1/DATA/d/s/t", true)]
    [InlineData("/api/v1/data/d/s/t/change-token", true)]
    [InlineData("/api/v1/catalog/datasets/a.b.c/sample-rows", true)]
    [InlineData("/api/v1/database", false)]
    [InlineData("/api/v1/data-nonexistent", false)]
    [InlineData("/api/v1/catalog", false)]
    [InlineData("/api/v1/catalog/datasets", false)]
    [InlineData("/api/v1/catalog/datasets/a.b.c", false)]
    [InlineData("/api/v1/sql", false)]
    [InlineData("/graphql", false)]
    public async Task Counts_only_the_closed_rest_data_family_with_a_segment_aware_match(string path, bool counted)
    {
        using var probe = new CounterProbe();
        var (mw, _, _, nextCalls) = Build();

        await mw.InvokeAsync(Ctx(path));

        (probe.RestData == 1).ShouldBe(counted);
        nextCalls().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_16_Anonymous_requests_are_counted_but_never_logged()
    {
        using var probe = new CounterProbe();
        var (mw, log, _, _) = Build();

        for (int i = 0; i < 20; i++) await mw.InvokeAsync(Ctx("/api/v1/data/x"));

        probe.RestData.ShouldBe(20);
        log.Lines.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_16_An_authenticated_principal_is_logged_once_per_window_without_secrets()
    {
        var (mw, log, time, _) = Build();
        const string sid = "S-1-5-21-111-222-333-1001";

        for (int i = 0; i < 5; i++) await mw.InvokeAsync(Ctx("/api/v1/data/secret-table?token=abc", "POST", sid, "?token=abc"));

        log.Lines.Count.ShouldBe(1);
        var line = log.Lines[0];
        line.ShouldStartWith("Warning|");
        line.ShouldContain("POST");
        line.ShouldNotContain(sid);
        line.ShouldNotContain("secret-table");
        line.ShouldNotContain("token");
        line.ShouldNotContain("abc");
        line.ShouldNotContain("/api/v1");

        time.Advance(TimeSpan.FromSeconds(61));
        await mw.InvokeAsync(Ctx("/api/v1/data/x", "GET", sid));
        log.Lines.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_16_The_same_principal_hashes_equal_and_different_principals_differ()
    {
        var (mw, log, _, _) = Build();

        await mw.InvokeAsync(Ctx("/api/v1/data/x", "GET", "alice"));
        await mw.InvokeAsync(Ctx("/api/v1/data/x", "GET", "bob"));

        log.Lines.Count.ShouldBe(2);
        log.Lines[0].ShouldNotBe(log.Lines[1]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_16_State_stays_bounded_and_log_lines_per_minute_are_capped_for_100000_distinct_principals()
    {
        using var probe = new CounterProbe();
        var (mw, log, time, _) = Build();

        for (int i = 0; i < 100_000; i++)
        {
            await mw.InvokeAsync(Ctx("/api/v1/data/x", "GET", $"principal-{i}"));
        }

        probe.RestData.ShouldBe(100_000);
        log.Lines.Count.ShouldBeLessThanOrEqualTo(DisabledReadPathObserverMiddleware.MaxLogLinesPerMinute);
        mw.TrackedPrincipalCount.ShouldBeLessThanOrEqualTo(DisabledReadPathObserverMiddleware.MaxTrackedPrincipals);

        time.Advance(TimeSpan.FromSeconds(61));
        await mw.InvokeAsync(Ctx("/api/v1/data/x", "GET", "someone-new"));
        log.Lines.Count.ShouldBeLessThanOrEqualTo(DisabledReadPathObserverMiddleware.MaxLogLinesPerMinute + 1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_16_The_observer_never_writes_a_response_and_always_calls_next()
    {
        var (mw, _, _, nextCalls) = Build();
        var ctx = Ctx("/api/v1/data/x", "GET", "alice");
        ctx.Response.Body = new System.IO.MemoryStream();

        await mw.InvokeAsync(ctx);

        nextCalls().ShouldBe(1);
        ctx.Response.StatusCode.ShouldBe(200);
        ctx.Response.Body.Length.ShouldBe(0);
        ctx.Response.HasStarted.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_16_An_internal_failure_does_not_change_the_response_and_is_counted()
    {
        using var probe = new CounterProbe();
        var calls = 0;
        var mw = new DisabledReadPathObserverMiddleware(_ => { calls++; return Task.CompletedTask; }, new ThrowingLogger(), new FakeTimeProvider());

        await mw.InvokeAsync(Ctx("/api/v1/data/x", "GET", "alice"));

        calls.ShouldBe(1);
        probe.Errors.ShouldBe(1);
    }

    private sealed class ThrowingLogger : ILogger<DisabledReadPathObserverMiddleware>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger down");
    }
}
