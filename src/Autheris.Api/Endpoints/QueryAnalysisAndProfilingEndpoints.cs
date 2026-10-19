namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// HTTP request payload for <c>POST /api/v1/governance/queries/analyze</c> (Issue #79).
/// </summary>
public sealed record AnalyzeQueriesHttpRequest(
    IReadOnlyList<QueryAnalysisItem> Queries,
    string? TargetSubjectId = null,
    IReadOnlyList<string>? TargetRoles = null);

/// <summary>
/// HTTP request payload for <c>POST /api/v1/governance/consents/bundle</c> (Issue #79).
/// </summary>
public sealed record SubmitBundledConsentHttpRequest(
    IReadOnlyList<RequestableConsentItem> Items,
    string Purpose,
    string? ConsumerReference = null,
    DateTimeOffset? RequestedUntilUtc = null);

/// <summary>
/// HTTP request payload for <c>POST /api/v1/catalog/datasets/{dataset}/profile</c> (Issue #80).
/// </summary>
public sealed record ComputeDatasetProfileHttpRequest(
    IReadOnlyList<Dictionary<string, object?>> SampleRows);

/// <summary>
/// Issues #79 &amp; #80: Zero-read static query analysis, bundled per-Data-Owner consent requests,
/// privacy-coarsened column value profiles, and synthetic sample rows endpoints.
/// </summary>
public static class QueryAnalysisAndProfilingEndpoints
{
    /// <param name="mapSampleRows">
    /// PLAN-SEC-115 C-115-1: the sample-rows route returns rows, so it belongs to the REST data family and is mapped only while
    /// <c>Gateway:RestApi:DataEndpoint:Enabled</c> is on.
    /// </param>
    /// <param name="mapQueryAnalysis">
    /// PLAN-GOV-240: dedicated switch for <c>POST /api/v1/governance/queries/analyze</c>, gated by <c>Gateway:Governance:QueryAnalysis:Enabled</c>.
    /// </param>
    public static IEndpointRouteBuilder MapQueryAnalysisAndProfilingEndpoints(
        this IEndpointRouteBuilder app,
        bool mapSampleRows = true,
        bool mapQueryAnalysis = true)
    {
        if (mapQueryAnalysis)
        {
            app.MapPost("/api/v1/governance/queries/analyze", HandleAnalyzeQueriesAsync)
                .WithName("AnalyzeQueriesStatic")
                .RequireAuthorization()
                .WithAudit(AuditLevel.Summarized, AuditEventTypes.QueryAnalysisEvaluated);
        }

        app.MapPost("/api/v1/governance/consents/bundle", HandleSubmitBundledConsentAsync)
            .WithName("SubmitBundledConsentRequest")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        if (mapSampleRows)
        {
            app.MapGet("/api/v1/catalog/datasets/{dataset}/sample-rows", HandleSampleRowsAsync)
                .WithName("GetDatasetSampleRowsWithSyntheticSupport")
                .RequireAuthorization()
                .WithAudit(AuditLevel.Summarized, AuditEventTypes.MetadataSyntheticSample);
        }

        return app;
    }

    internal static async Task<IResult> HandleAnalyzeQueriesAsync(
        [FromBody] AnalyzeQueriesHttpRequest? request,
        HttpContext httpContext,
        IQueryAnalysisService analysisService,
        CancellationToken ct)
    {
        if (request?.Queries is null || request.Queries.Count == 0)
        {
            return SafeErrorResults.BadRequest("INVALID_ANALYSIS_REQUEST", "At least one query item is required.", httpContext);
        }

        ClaimsPrincipal? targetPrincipal = null;
        if (!string.IsNullOrWhiteSpace(request.TargetSubjectId))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, request.TargetSubjectId),
                new(ClaimTypes.Name, request.TargetSubjectId)
            };

            if (request.TargetRoles != null)
            {
                foreach (var role in request.TargetRoles)
                {
                    if (!string.IsNullOrWhiteSpace(role))
                    {
                        claims.Add(new Claim(ClaimTypes.Role, role));
                    }
                }
            }

            targetPrincipal = new ClaimsPrincipal(new ClaimsIdentity(claims, "SimulatedTarget"));
        }

        try
        {
            var result = await analysisService.AnalyzeQueriesAsync(
                request.Queries,
                httpContext.User,
                targetPrincipal,
                ct).ConfigureAwait(false);

            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "FORBIDDEN");
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN", "Access denied by governance policy.", httpContext);
        }
    }

    internal static async Task<IResult> HandleSubmitBundledConsentAsync(
        [FromBody] SubmitBundledConsentHttpRequest? request,
        HttpContext httpContext,
        IBundledConsentRequestService bundledConsentService,
        CancellationToken ct)
    {
        if (request?.Items is null || request.Items.Count == 0)
        {
            return SafeErrorResults.BadRequest("INVALID_CONSENT_BUNDLE", "At least one consent item is required.", httpContext);
        }

        try
        {
            var result = await bundledConsentService.SubmitBundledRequestAsync(
                httpContext.User,
                new BundledConsentRequest(
                    Items: request.Items,
                    Purpose: request.Purpose,
                    ConsumerReference: request.ConsumerReference,
                    RequestedUntilUtc: request.RequestedUntilUtc),
                ct).ConfigureAwait(false);

            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "NOT_FOUND");
            return SafeErrorResults.Problem(StatusCodes.Status404NotFound, "NOT_FOUND", "One or more requested datasets were not found.", httpContext);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "FORBIDDEN");
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN", "Access denied by governance policy.", httpContext);
        }
    }

    internal static async Task<IResult> HandleSampleRowsAsync(
        string dataset,
        [FromQuery] int? count,
        [FromQuery] bool? synthetic,
        HttpContext httpContext,
        IMcpDatasetCatalog mcpCatalog,
        CancellationToken ct)
    {
        try
        {
            var result = await mcpCatalog.SampleRowsAsync(
                httpContext.User,
                dataset,
                count ?? 5,
                synthetic ?? false,
                ct).ConfigureAwait(false);

            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "NOT_FOUND");
            return SafeErrorResults.Problem(StatusCodes.Status404NotFound, "NOT_FOUND", "Requested dataset was not found.", httpContext);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "FORBIDDEN");
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN", "Access denied by governance policy.", httpContext);
        }
    }
}
