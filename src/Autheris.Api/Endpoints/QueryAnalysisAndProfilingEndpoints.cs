namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Catalog.Profiling;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public static IServiceCollection AddQueryAnalysisAndProfilingServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IColumnValueProfileService, ColumnValueProfileService>();
        services.TryAddSingleton<ISyntheticSampleRowGenerator, SyntheticSampleRowGenerator>();
        services.TryAddScoped<IQueryAnalysisService, QueryAnalysisService>();
        services.TryAddScoped<IBundledConsentRequestService, BundledConsentRequestService>();
        return services;
    }

    public static IEndpointRouteBuilder MapQueryAnalysisAndProfilingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/governance/queries/analyze", HandleAnalyzeQueriesAsync)
            .WithName("AnalyzeQueriesStatic")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.QueryAnalysisEvaluated);

        app.MapPost("/api/v1/governance/consents/bundle", HandleSubmitBundledConsentAsync)
            .WithName("SubmitBundledConsentRequest")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        app.MapGet("/api/v1/catalog/datasets/{dataset}/sample-rows", HandleSampleRowsAsync)
            .WithName("GetDatasetSampleRowsWithSyntheticSupport")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.MetadataSyntheticSample);

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
            return Results.BadRequest(new { error = "At least one query item is required." });
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
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Access denied by governance policy.", code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
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
            return Results.BadRequest(new { error = "At least one consent item is required." });
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
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "One or more requested datasets were not found." });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Access denied by governance policy.", code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
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
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Requested dataset was not found." });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Access denied by governance policy.", code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
