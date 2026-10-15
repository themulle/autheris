namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using Autheris.Api.Extensions;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Application.Jobs.Services;
using Autheris.Application.State;
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
            AsyncJobDescriptor job;
            try
            {
                job = await jobManager.SubmitJobAsync(request, httpContext.User, tenantId, ct).ConfigureAwait(false);
            }
            catch (ClusterStateUnavailableException)
            {
                return StateUnavailable();
            }
            catch (AsyncJobQueueLimitExceededException)
            {
                httpContext.Response.Headers.RetryAfter = "30";
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many queued async jobs.",
                    detail: "The tenant has reached the maximum number of queued or running async jobs. Retry later.");
            }

            MarkDomainAudited(httpContext);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Delegated, AuditEventTypes.AsyncJobSubmitted);

        group.MapGet("/{jobId}/status", async (
            string jobId,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var userSid = AsyncJobOwner.Resolve(httpContext.User) ?? string.Empty;

            AsyncJobDescriptor? job;
            try
            {
                job = await jobManager.GetJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            }
            catch (ClusterStateUnavailableException)
            {
                return StateUnavailable();
            }

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
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.AsyncJobStatusRead);

        group.MapGet("/{jobId}/result", async (
            string jobId,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var userSid = AsyncJobOwner.Resolve(httpContext.User) ?? string.Empty;

            Stream? stream;
            AsyncJobDescriptor? job;
            try
            {
                stream = await jobManager.GetJobResultStreamAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
                if (stream == null)
                {
                    return Results.NotFound();
                }

                MarkDomainAudited(httpContext);
                job = await jobManager.GetJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            }
            catch (ClusterStateUnavailableException)
            {
                return StateUnavailable();
            }
            catch (AsyncJobResultUnavailableException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The job result is not available on this node.",
                    detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = ex.ErrorCode });
            }

            var format = job?.Format ?? "json";
            var contentType = format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "application/json";
            var fileName = $"query-result-{jobId}.{format}";

            return Results.File(stream, contentType, fileDownloadName: fileName);
        }).RequireAuthorization().WithAudit(AuditLevel.Delegated, AuditEventTypes.AsyncJobDownloaded);

        group.MapDelete("/{jobId}", async (
            string jobId,
            IAsyncQueryJobManager jobManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var userSid = AsyncJobOwner.Resolve(httpContext.User) ?? string.Empty;

            bool cancelled;
            try
            {
                cancelled = await jobManager.CancelJobAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
            }
            catch (ClusterStateUnavailableException)
            {
                return StateUnavailable();
            }

            if (cancelled)
            {
                MarkDomainAudited(httpContext);
            }

            return cancelled ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization().WithAudit(AuditLevel.Delegated, AuditEventTypes.AsyncJobCancelled);

        return app;
    }

    private static IResult StateUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "The shared job state is temporarily unavailable.",
        detail: "The cluster state store could not be reached. Retry later.");

    /// <summary>
    /// Delegated audit routes: the job manager has written the ALLOW audit record (fail-closed), so the central
    /// middleware must not write a second one. Denials and failures are NOT marked and stay with the middleware.
    /// </summary>
    private static void MarkDomainAudited(HttpContext httpContext) =>
        httpContext.Features.Get<AuditContext>()?.MarkHandled();
}
