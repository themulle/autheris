namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.ResultCache;
using Autheris.Application.Common.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Autheris.Tests.Unit.Sql.ResultCache;
using Shouldly;
using Xunit;

/// <summary>Endpoint side of the WebSQL result cache: request option, request flag, response field and headers.</summary>
public sealed class WebSqlResultCacheEndpointTests
{
    private static GatewayOptions CacheOn() => new() { WebSql = new WebSqlOptions { ResultCacheMaxMegabytes = 64 } };

    private static DefaultHttpContext CreateContext(string body, string queryString = "", bool withParquet = false)
    {
        var services = new ServiceCollection();
        if (withParquet)
        {
            services.AddSingleton<IParquetExportService>(new Autheris.Application.Common.Serialization.ParquetExportService(
                Options.Create(new GatewayOptions()), NullLogger<Autheris.Application.Common.Serialization.ParquetExportService>.Instance));
        }

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = "/api/v1/sql";
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(queryString);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user_1"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-RC"),
            new Claim("tenant_id", "tenant_a")
        ], "Test"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(string? resultCache = null, string? extra = null)
    {
        var sb = new StringBuilder("{\"sql\":\"SELECT id FROM t\"");
        if (resultCache != null)
        {
            sb.Append(",\"resultCache\":\"").Append(resultCache).Append('"');
        }

        if (extra != null)
        {
            sb.Append(',').Append(extra);
        }

        return sb.Append('}').ToString();
    }

    private static JsonDocument ReadJson(HttpContext context)
    {
        var stream = (MemoryStream)context.Response.Body;
        return JsonDocument.Parse(stream.ToArray());
    }

    private static Task RunAsync(HttpContext context, FakeGovernedSqlService service, GatewayOptions options) =>
        WebSqlEndpoints.HandleWebSqlRequest(context, service, Options.Create(options), NullLoggerFactory.Instance);

    [Fact]
    [Trait("Category", "Security")]
    public async Task Response_field_absent_when_globally_off_SECRC08()
    {
        var service = new FakeGovernedSqlService(new WebSqlResultCacheOutcome(WebSqlResultCacheStatus.Hit, 3));
        var context = CreateContext(Body());

        await RunAsync(context, service, new GatewayOptions());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        service.LastRequest!.ResultCache.ShouldBeNull();
        using var doc = ReadJson(context);
        doc.RootElement.TryGetProperty("resultCache", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Response_field_present_when_on_and_outcome_reported()
    {
        var service = new FakeGovernedSqlService(new WebSqlResultCacheOutcome(WebSqlResultCacheStatus.Hit, 7));
        var context = CreateContext(Body());

        await RunAsync(context, service, CacheOn());

        service.LastRequest!.ResultCache.ShouldNotBeNull();
        service.LastRequest.ResultCache!.Format.ShouldBe("json");
        service.LastRequest.ResultCache.Mode.ShouldBe(WebSqlResultCacheMode.Default);
        using var doc = ReadJson(context);
        var field = doc.RootElement.GetProperty("resultCache");
        field.GetProperty("status").GetString().ShouldBe("hit");
        field.GetProperty("ageSeconds").GetInt32().ShouldBe(7);
        field.TryGetProperty("reason", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(WebSqlResultCacheStatus.Miss, "miss")]
    [InlineData(WebSqlResultCacheStatus.Store, "store")]
    [InlineData(WebSqlResultCacheStatus.Bypass, "bypass")]
    [InlineData(WebSqlResultCacheStatus.Ineligible, "ineligible")]
    public async Task Response_field_status_text_follows_the_outcome(WebSqlResultCacheStatus status, string expected)
    {
        var service = new FakeGovernedSqlService(new WebSqlResultCacheOutcome(status, null, "some_reason"));
        var context = CreateContext(Body());

        await RunAsync(context, service, CacheOn());

        using var doc = ReadJson(context);
        var field = doc.RootElement.GetProperty("resultCache");
        field.GetProperty("status").GetString().ShouldBe(expected);
        field.GetProperty("reason").GetString().ShouldBe("some_reason");
        field.TryGetProperty("ageSeconds", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Response_field_absent_when_on_but_no_outcome_was_reported()
    {
        var service = new FakeGovernedSqlService(null);
        var context = CreateContext(Body());

        await RunAsync(context, service, CacheOn());

        using var doc = ReadJson(context);
        doc.RootElement.TryGetProperty("resultCache", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Arrays_format_carries_the_field_too()
    {
        var service = new FakeGovernedSqlService(new WebSqlResultCacheOutcome(WebSqlResultCacheStatus.Store));
        var context = CreateContext(Body(), "?format=arrays");

        await RunAsync(context, service, CacheOn());

        using var doc = ReadJson(context);
        doc.RootElement.GetProperty("resultCache").GetProperty("status").GetString().ShouldBe("store");
    }

    [Theory]
    [InlineData("bypass", WebSqlResultCacheMode.Bypass)]
    [InlineData("BYPASS", WebSqlResultCacheMode.Bypass)]
    [InlineData("default", WebSqlResultCacheMode.Default)]
    [InlineData("Default", WebSqlResultCacheMode.Default)]
    public async Task Request_option_is_case_insensitive(string value, WebSqlResultCacheMode expected)
    {
        var service = new FakeGovernedSqlService(null);
        var context = CreateContext(Body(value));

        await RunAsync(context, service, CacheOn());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        service.LastRequest!.ResultCache!.Mode.ShouldBe(expected);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bypass_invalid_value_400_no_echo_SECRC18(bool cacheOn)
    {
        var service = new FakeGovernedSqlService(null);
        var context = CreateContext(Body("zz-evil-marker-zz"));

        await RunAsync(context, service, cacheOn ? CacheOn() : new GatewayOptions());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        service.Calls.ShouldBe(0);
        var text = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        text.ShouldNotContain("zz-evil-marker-zz");
    }

    [Fact]
    public async Task Wait_timeout_statement_path_is_never_flagged()
    {
        var manager = new FakeStatementManager();
        var context = CreateContext(Body(), "?wait_timeout=5s");
        context.RequestServices = new ServiceCollection().AddSingleton<IWebSqlStatementManager>(manager).BuildServiceProvider();

        await RunAsync(context, new FakeGovernedSqlService(null), CacheOn());

        manager.Submitted.ShouldNotBeNull();
        manager.Submitted!.ResultCache.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cache_control_no_store_on_hit_json_and_parquet_SECRC21()
    {
        var options = CacheOn();
        var hit = new WebSqlResultCacheOutcome(WebSqlResultCacheStatus.Hit, 1);

        // JSON: the response header comes from the security headers middleware around the endpoint.
        var jsonContext = CreateContext(Body());
        var middleware = new SecurityHeadersMiddleware(
            ctx => RunAsync(ctx, new FakeGovernedSqlService(hit), options), options, new TestHostEnvironment());
        await middleware.InvokeAsync(jsonContext);
        jsonContext.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        using (var doc = ReadJson(jsonContext))
        {
            doc.RootElement.GetProperty("resultCache").GetProperty("status").GetString().ShouldBe("hit");
        }

        // Parquet: written by the Parquet response writer.
        var parquetContext = CreateContext(Body(), "?format=parquet", withParquet: true);
        await RunAsync(parquetContext, new FakeGovernedSqlService(hit), options);
        parquetContext.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
    }

    [Fact]
    public async Task Parquet_without_paging_is_flagged_and_served_without_a_body_signal()
    {
        var service = new FakeGovernedSqlService(new WebSqlResultCacheOutcome(WebSqlResultCacheStatus.Hit, 2));
        var context = CreateContext(Body("bypass"), "?format=parquet", withParquet: true);

        await RunAsync(context, service, CacheOn());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        service.LastRequest!.ResultCache.ShouldNotBeNull();
        service.LastRequest.ResultCache!.Format.ShouldBe("parquet");
        service.LastRequest.ResultCache.Mode.ShouldBe(WebSqlResultCacheMode.Bypass);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        bytes.Length.ShouldBeGreaterThan(4);
        Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("PAR1");
        Encoding.UTF8.GetString(bytes).ShouldNotContain("resultCache");
    }

    [Fact]
    public async Task Parquet_is_not_flagged_when_the_cache_is_globally_off()
    {
        var service = new FakeGovernedSqlService(null);
        var context = CreateContext(Body(), "?format=parquet", withParquet: true);

        await RunAsync(context, service, new GatewayOptions());

        service.LastRequest!.ResultCache.ShouldBeNull();
    }

    [Fact]
    public async Task Truncation_is_reported_for_a_probe_reader_that_is_not_the_row_limited_reader()
    {
        var service = new FakeGovernedSqlService(null, probeHasMore: true);
        var context = CreateContext(Body());

        await RunAsync(context, service, CacheOn());

        using var doc = ReadJson(context);
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    private sealed class FakeGovernedSqlService(WebSqlResultCacheOutcome? outcome, bool probeHasMore = false) : IGovernedSqlExecutionService
    {
        public GovernedSqlQueryRequest? LastRequest { get; private set; }

        public int Calls { get; private set; }

        public async Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
            CancellationToken ct = default)
        {
            Calls++;
            LastRequest = request;
            // The cached replay reader is a row-limit probe that is not the row-limited reader.
            using DbDataReader reader = probeHasMore
                ? new CachedResultDataReader(ResultCacheFixtures.Result(2, truncated: true))
                : ResultCacheFixtures.Rows(1);
            var info = new GovernedResultInfo(SqlResultColumns.Describe(reader), [.. new bool[reader.FieldCount]]);
            await rowWriter(reader, info, ct);
            if (outcome is not null)
            {
                request.ResultCache?.OnOutcome?.Invoke(outcome);
            }
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeStatementManager : IWebSqlStatementManager
    {
        public GovernedSqlQueryRequest? Submitted { get; private set; }

        public Task<StatementExecutionStatus> SubmitOrWaitAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal user, Sid owner, TenantId tenantId, TimeSpan waitTimeout,
            Autheris.Domain.Options.DataSources.DataSourceTransportFlags ingress = Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None,
            CancellationToken ct = default)
        {
            Submitted = request;
            return Task.FromResult(new StatementExecutionStatus("s1", "FINISHED", ["id"], [], 1));
        }

        public Task<StatementExecutionStatus> GetStatusOrWaitAsync(
            string statementId, ClaimsPrincipal user, Sid owner, TenantId tenantId, TimeSpan waitTimeout,
            Autheris.Domain.Options.DataSources.DataSourceTransportFlags ingress = Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> CancelStatementAsync(string statementId, ClaimsPrincipal user, Sid owner, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
