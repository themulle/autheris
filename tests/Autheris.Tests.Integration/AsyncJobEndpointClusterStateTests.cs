namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Jobs.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-A-12: an unavailable cluster state or a node-local result surfaces as HTTP 503, never as 404 or 500.</summary>
public sealed class AsyncJobEndpointClusterStateTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AsyncJobEndpointClusterStateTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAsyncQueryJobManager, UnavailableJobManager>();
            });
        });
    }

    private sealed class UnavailableJobManager : IAsyncQueryJobManager
    {
        public Task<AsyncJobDescriptor> SubmitJobAsync(AsyncQueryJobRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new ClusterStateUnavailableException();

        public Task<AsyncJobDescriptor?> GetJobAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default) =>
            throw new ClusterStateUnavailableException();

        public Task<Stream?> GetJobResultStreamAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default) =>
            jobId.StartsWith("nodelocal", StringComparison.Ordinal) ? throw new AsyncJobResultUnavailableException() : throw new ClusterStateUnavailableException();

        public Task<bool> CancelJobAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default) =>
            throw new ClusterStateUnavailableException();

        public Task PurgeExpiredJobsAsync(TimeSpan retentionPeriod, CancellationToken ct = default) => Task.CompletedTask;
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "alice");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "User");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        return client;
    }

    [Fact]
    public async Task NodeB_StateUnavailable_Endpoint_Returns503()
    {
        var client = Client();
        var id = Guid.NewGuid().ToString("D");

        (await client.GetAsync($"/api/v1/jobs/{id}/status")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync($"/api/v1/jobs/{id}/result")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.DeleteAsync($"/api/v1/jobs/{id}")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsJsonAsync("/api/v1/jobs/query", new AsyncQueryJobRequest("SELECT 1"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task NodeLocalResult_Endpoint_Returns503WithCode()
    {
        var response = await Client().GetAsync("/api/v1/jobs/nodelocal-1/result");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("ASYNC_JOB_RESULT_NODE_LOCAL");
    }
}
