namespace Autheris.Api.Endpoints;

using System;
using System.Security.Claims;
using System.Threading;
using Autheris.Api.Extensions;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class AsyncJobEndpoints
{
    public static IEndpointRouteBuilder MapAsyncJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobs");

        group.MapPost("/query", async (
            AsyncQueryJobRequest request,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var job = await jobManager.SubmitJobAsync(request, httpContext.User, tenantId, ct).ConfigureAwait(false);

            var response = new AsyncJobStatusResponse(
                JobId: job.JobId,
                State: job.State,
                CreatedAt: job.CreatedAt,
                StartedAt: null,
                CompletedAt: null,
                RowsProduced: null,
                BytesProduced: null,
                ErrorMessage: null,
                ResultDownloadUrl: $"/api/v1/jobs/{job.JobId}/result");

            return Results.Accepted($"/api/v1/jobs/{job.JobId}/status", response);
        }).RequireAuthorization();

        group.MapGet("/{jobId}/status", async (
            string jobId,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var userSid = httpContext.User.GetUserSid()?.Value
                          ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? string.Empty;

            var job = await jobManager.GetJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            if (job == null)
            {
                return Results.NotFound();
            }

            var downloadUrl = job.State == AsyncJobState.Completed
                ? $"/api/v1/jobs/{job.JobId}/result"
                : null;

            var response = new AsyncJobStatusResponse(
                JobId: job.JobId,
                State: job.State,
                CreatedAt: job.CreatedAt,
                StartedAt: job.StartedAt,
                CompletedAt: job.CompletedAt,
                RowsProduced: job.RowsProduced,
                BytesProduced: job.BytesProduced,
                ErrorMessage: job.ErrorMessage,
                ResultDownloadUrl: downloadUrl);

            return Results.Ok(response);
        }).RequireAuthorization();

        group.MapGet("/{jobId}/result", async (
            string jobId,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var userSid = httpContext.User.GetUserSid()?.Value
                          ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? string.Empty;

            var stream = await jobManager.GetJobResultStreamAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            if (stream == null)
            {
                return Results.NotFound();
            }

            var job = await jobManager.GetJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            var format = job?.Format ?? "json";
            var contentType = format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "application/json";
            var fileName = $"query-result-{jobId}.{format}";

            return Results.File(stream, contentType, fileDownloadName: fileName);
        }).RequireAuthorization();

        group.MapDelete("/{jobId}", async (
            string jobId,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var userSid = httpContext.User.GetUserSid()?.Value
                          ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? string.Empty;

            var cancelled = await jobManager.CancelJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            return cancelled ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        return app;
    }
}
