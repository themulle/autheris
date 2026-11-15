namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Runtime.Jobs.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>ARCH-MNT WP4.3 regression: the real async-job endpoint mapping completes and registers its routes.</summary>
public sealed class AsyncJobEndpointMappingIntegrationTests
{
    [Fact]
    public void MapAsyncJobEndpoints_OnMinimalWebApplication_RegistersJobRoutes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IAsyncQueryJobManager>(new StubAsyncQueryJobManager());
        using var app = builder.Build();

        AsyncJobEndpoints.MapAsyncJobEndpoints(app);

        var patterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        patterns.ShouldContain("/api/v1/jobs/query");
    }

    private sealed class StubAsyncQueryJobManager : IAsyncQueryJobManager
    {
        public Task<AsyncJobDescriptor> SubmitJobAsync(
            AsyncQueryJobRequest request, ClaimsPrincipal user, TenantId tenantId,
            DataSourceTransportFlags ingress = DataSourceTransportFlags.None, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AsyncJobDescriptor?> GetJobAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default) =>
            Task.FromResult<AsyncJobDescriptor?>(null);

        public Task<Stream?> GetJobResultStreamAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(null);

        public Task<bool> CancelJobAsync(string jobId, TenantId tenantId, string userSid, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task PurgeExpiredJobsAsync(TimeSpan retentionPeriod, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
