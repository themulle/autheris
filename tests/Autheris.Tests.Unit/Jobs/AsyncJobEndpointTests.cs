namespace Autheris.Tests.Unit.Jobs;

using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Runtime.Jobs.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AsyncJobEndpointTests
{
    private sealed class FakeAsyncQueryJobManager : IAsyncQueryJobManager
    {
        public bool SubmitCalled { get; private set; }
        public AsyncJobDescriptor? StoredJob { get; set; }
        public byte[] ResultBytes { get; set; } = "{}"u8.ToArray();

        public Task<AsyncJobDescriptor> SubmitJobAsync(
            AsyncQueryJobRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            Autheris.Domain.Options.DataSources.DataSourceTransportFlags ingress = Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None,
            CancellationToken ct = default)
        {
            SubmitCalled = true;
            return Task.FromResult(new AsyncJobDescriptor(
                JobId: "job-1",
                TenantId: tenantId.Value,
                SubmittedByUserId: "S-1-USER",
                Query: request.Query,
                Format: request.Format ?? "json",
                State: AsyncJobState.Queued,
                CreatedAt: DateTimeOffset.UtcNow));
        }

        public Task<AsyncJobDescriptor?> GetJobAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default)
            => Task.FromResult(StoredJob);

        public Task<Stream?> GetJobResultStreamAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default)
            => Task.FromResult<Stream?>(new MemoryStream(ResultBytes));

        public Task<bool> CancelJobAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task PurgeExpiredJobsAsync(TimeSpan retentionPeriod, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "tenant-a")],
                "Test"))
        };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        var text = await new StreamReader(response.Body).ReadToEndAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Unknown_format_returns_400()
    {
        var manager = new FakeAsyncQueryJobManager();
        var ctx = NewContext();

        var result = await AsyncJobEndpoints.SubmitJobHandler(
            new AsyncQueryJobRequest("SELECT 1", Format: "parquet"),
            manager,
            ctx,
            CancellationToken.None);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        manager.SubmitCalled.ShouldBeFalse();
        var json = await ReadJsonAsync(ctx.Response);
        json.GetProperty("code").GetString().ShouldBe("invalid_format");
    }

    [Fact]
    public async Task Overlong_format_returns_400()
    {
        var manager = new FakeAsyncQueryJobManager();
        var ctx = NewContext();

        var result = await AsyncJobEndpoints.SubmitJobHandler(
            new AsyncQueryJobRequest("SELECT 1", Format: new string('a', 10_000)),
            manager,
            ctx,
            CancellationToken.None);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        manager.SubmitCalled.ShouldBeFalse();
        var json = await ReadJsonAsync(ctx.Response);
        json.GetProperty("code").GetString().ShouldBe("invalid_format");
    }

    [Fact]
    public async Task Download_name_ignores_stored_format()
    {
        var manager = new FakeAsyncQueryJobManager();

        // Stored uppercase/padded format produces fixed constant extension, never the raw stored string.
        manager.StoredJob = new AsyncJobDescriptor(
            "job-1", "tenant-a", "S-1-USER", "SELECT 1", "  CSV ", AsyncJobState.Completed, DateTimeOffset.UtcNow);
        var okCtx = NewContext();
        var okResult = await AsyncJobEndpoints.DownloadJobResultHandler("job-1", manager, okCtx, CancellationToken.None);
        await okResult.ExecuteAsync(okCtx);

        okCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        okCtx.Response.ContentType.ShouldBe("text/csv");
        okCtx.Response.Headers.ContentDisposition.ToString().ShouldContain("query-result-job-1.csv");

        // Tampered / unknown stored format fails with 500 fixed Problem (no fallback).
        manager.StoredJob = new AsyncJobDescriptor(
            "job-1", "tenant-a", "S-1-USER", "SELECT 1", "../evil", AsyncJobState.Completed, DateTimeOffset.UtcNow);
        var badCtx = NewContext();
        var badResult = await AsyncJobEndpoints.DownloadJobResultHandler("job-1", manager, badCtx, CancellationToken.None);
        await badResult.ExecuteAsync(badCtx);

        badCtx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var json = await ReadJsonAsync(badCtx.Response);
        json.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status500InternalServerError);
        json.ToString().ShouldNotContain("../evil");
    }
}
