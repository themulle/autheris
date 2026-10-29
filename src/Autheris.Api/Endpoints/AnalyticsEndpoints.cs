namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Analytics;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// REST endpoints for governed analytical aggregations (#28, #37) and dbt Semantic Layer metric execution (#38).
/// </summary>
public static class AnalyticsEndpoints
{
    public sealed record AggregateSelectionDto(
        string Function,
        string? Column = null,
        string? Alias = null);

    public sealed record AggregateRequestDto(
        string Dataset,
        IReadOnlyList<string>? GroupBy = null,
        IReadOnlyList<AggregateSelectionDto>? Aggregates = null,
        string? Where = null,
        IReadOnlyList<string>? OrderBy = null,
        int? Limit = null);

    public sealed record MetricQueryRequestDto(
        IReadOnlyList<string> Metrics,
        IReadOnlyList<string>? GroupBy = null,
        string? Where = null,
        string? TimeGranularity = null,
        IReadOnlyList<string>? OrderBy = null,
        int? Limit = null);

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/analytics/aggregate", HandleAggregateAsync)
            .WithName("ExecuteGovernedAggregation")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.AggregateQuery);

        app.MapPost("/api/v1/analytics/metrics/query", HandleQueryMetricsAsync)
            .WithName("ExecuteSemanticMetricQuery")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.MetricQuery);

        app.MapGet("/api/v1/analytics/metrics", HandleListMetricsAsync)
            .WithName("ListSemanticMetrics")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    private const string NotSupportedMessage = "The requested aggregation is not supported for this dataset.";

    /// <summary>
    /// #389: translation limits of the governed compiler. Time-zone invalid/argument/unavailable are deliberately excluded and keep reaching
    /// the global exception handler (I389-SC-06).
    /// </summary>
    private static bool IsNotSupportedRefusal(Exception ex) => ex is TrinoSqlEngine.Ast.Builder.AstBuildException or TrinoSqlEngine.SqlCompileNotSupportedException
        or TrinoSqlEngine.SqlTimeZoneException
        {
            Code: not (TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.TimeZoneUnavailable or TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.TimeZoneInvalid or TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.TimeZoneArgument)
        };

    internal static async Task<IResult> HandleAggregateAsync(
        AggregateRequestDto request,
        HttpContext httpContext,
        IGovernedAggregationService aggregationService,
        CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Dataset) || !TableIdentifier.TryParse(request.Dataset, out var tableId))
        {
            return SafeErrorResults.BadRequest("INVALID_DATASET", "A valid dataset identifier (domain.schema.table) is required.", httpContext);
        }

        try
        {
            var selections = new List<AggregateSelection>();
            if (request.Aggregates is { Count: > 0 })
            {
                foreach (var dto in request.Aggregates)
                {
                    if (!Enum.TryParse<AggregateFunction>(dto.Function, ignoreCase: true, out var fn))
                    {
                        return SafeErrorResults.BadRequest("INVALID_AGGREGATE_FUNCTION", "One or more requested aggregate functions are not supported.", httpContext);
                    }

                    selections.Add(new AggregateSelection(fn, dto.Column, dto.Alias));
                }
            }

            TableFilterClause? whereClause = !string.IsNullOrWhiteSpace(request.Where)
                ? TableFilterClause.FromTrinoText(request.Where, Array.Empty<string>())
                : null;

            var orderBy = ParseOrderByList(request.OrderBy);
            var query = new AggregationQuery(
                Table: tableId,
                GroupBy: request.GroupBy,
                Aggregates: selections,
                Where: whereClause,
                OrderBy: orderBy,
                Limit: request.Limit,
                Transport: SqlTransports.Rest);

            var result = await aggregationService.ExecuteAggregationAsync(httpContext.User, query, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (PrivacyBudgetExhaustedException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "PRIVACY_BUDGET_EXHAUSTED");
            return SafeErrorResults.Problem(
                StatusCodes.Status429TooManyRequests,
                "PRIVACY_BUDGET_EXHAUSTED",
                "The daily differential privacy epsilon budget has been exhausted.",
                httpContext);
        }
        catch (MaskedAggregationException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, MaskedAggregationException.ErrorCodeConst);
            return SafeErrorResults.Problem(
                StatusCodes.Status403Forbidden,
                MaskedAggregationException.ErrorCodeConst,
                "Aggregation or grouping on a masked column is not permitted.",
                httpContext);
        }
        catch (AggregationColumnUnavailableException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, AggregationColumnUnavailableException.ErrorCodeConst);
            return SafeErrorResults.Problem(
                StatusCodes.Status403Forbidden,
                AggregationColumnUnavailableException.ErrorCodeConst,
                AggregationColumnUnavailableException.FixedMessage,
                httpContext);
        }
        catch (AggregationFilterRejectedException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, AggregationFilterRejectedException.ErrorCodeConst);
            return SafeErrorResults.BadRequest(AggregationFilterRejectedException.ErrorCodeConst, AggregationFilterRejectedException.FixedMessage, httpContext);
        }
        catch (AggregationRowCapExceededException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, AggregationRowCapExceededException.ErrorCodeConst);
            return SafeErrorResults.BadRequest(AggregationRowCapExceededException.ErrorCodeConst, AggregationRowCapExceededException.FixedMessage, httpContext);
        }
        catch (AggregationPrivacyRestrictedException ex)
        {
            ApiProblems.LogRefusal(httpContext, ex.GetType(), AggregationPrivacyRestrictedException.ErrorCodeConst);
            return SafeErrorResults.BadRequest(AggregationPrivacyRestrictedException.ErrorCodeConst, AggregationPrivacyRestrictedException.FixedMessage, httpContext);
        }
        catch (Exception ex) when (IsNotSupportedRefusal(ex))
        {
            ApiProblems.LogRefusal(httpContext, ex.GetType(), AggregationErrorCodes.NotSupported);
            return SafeErrorResults.Problem(StatusCodes.Status422UnprocessableEntity, AggregationErrorCodes.NotSupported, NotSupportedMessage, httpContext);
        }
        catch (GatewayInvalidQueryException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.BadRequest(ex.ErrorCode, "The aggregation query parameters are invalid.", httpContext);
        }
        catch (TableNotFoundException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "NOT_FOUND");
            return SafeErrorResults.Problem(StatusCodes.Status404NotFound, "NOT_FOUND", "The requested dataset was not found.", httpContext);
        }
        catch (GatewaySecurityException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, ex.ErrorCode, "Access to the requested aggregation was denied by governance policy.", httpContext);
        }
        catch (ArgumentException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "INVALID_AGGREGATION_QUERY");
            return SafeErrorResults.BadRequest("INVALID_AGGREGATION_QUERY", "The aggregation query parameters are invalid.", httpContext);
        }
    }

    internal static async Task<IResult> HandleQueryMetricsAsync(
        MetricQueryRequestDto request,
        HttpContext httpContext,
        ISemanticMetricQueryService metricQueryService,
        CancellationToken ct)
    {
        if (request?.Metrics is not { Count: > 0 })
        {
            return SafeErrorResults.BadRequest("INVALID_METRICS_REQUEST", "At least one metric name must be specified.", httpContext);
        }

        try
        {
            TableFilterClause? whereClause = !string.IsNullOrWhiteSpace(request.Where)
                ? TableFilterClause.FromTrinoText(request.Where, Array.Empty<string>())
                : null;

            var orderBy = ParseOrderByList(request.OrderBy);
            var metricRequest = new MetricQueryRequest(
                Metrics: request.Metrics,
                GroupBy: request.GroupBy,
                Where: whereClause,
                TimeGranularity: request.TimeGranularity,
                OrderBy: orderBy,
                Limit: request.Limit);

            var result = await metricQueryService.QueryMetricsAsync(httpContext.User, metricRequest, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (PrivacyBudgetExhaustedException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "PRIVACY_BUDGET_EXHAUSTED");
            return SafeErrorResults.Problem(
                StatusCodes.Status429TooManyRequests,
                "PRIVACY_BUDGET_EXHAUSTED",
                "The daily differential privacy epsilon budget has been exhausted.",
                httpContext);
        }
        catch (MaskedAggregationException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, MaskedAggregationException.ErrorCodeConst);
            return SafeErrorResults.Problem(
                StatusCodes.Status403Forbidden,
                MaskedAggregationException.ErrorCodeConst,
                "Metric measure or dimension references a column that is masked for the caller.",
                httpContext);
        }
        catch (AggregationColumnUnavailableException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, AggregationColumnUnavailableException.ErrorCodeConst);
            return SafeErrorResults.Problem(
                StatusCodes.Status403Forbidden,
                AggregationColumnUnavailableException.ErrorCodeConst,
                AggregationColumnUnavailableException.FixedMessage,
                httpContext);
        }
        catch (AggregationFilterRejectedException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, AggregationFilterRejectedException.ErrorCodeConst);
            return SafeErrorResults.BadRequest(AggregationFilterRejectedException.ErrorCodeConst, AggregationFilterRejectedException.FixedMessage, httpContext);
        }
        catch (AggregationRowCapExceededException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, AggregationRowCapExceededException.ErrorCodeConst);
            return SafeErrorResults.BadRequest(AggregationRowCapExceededException.ErrorCodeConst, AggregationRowCapExceededException.FixedMessage, httpContext);
        }
        catch (AggregationPrivacyRestrictedException ex)
        {
            ApiProblems.LogRefusal(httpContext, ex.GetType(), AggregationPrivacyRestrictedException.ErrorCodeConst);
            return SafeErrorResults.BadRequest(AggregationPrivacyRestrictedException.ErrorCodeConst, AggregationPrivacyRestrictedException.FixedMessage, httpContext);
        }
        catch (Exception ex) when (IsNotSupportedRefusal(ex))
        {
            ApiProblems.LogRefusal(httpContext, ex.GetType(), AggregationErrorCodes.NotSupported);
            return SafeErrorResults.Problem(StatusCodes.Status422UnprocessableEntity, AggregationErrorCodes.NotSupported, NotSupportedMessage, httpContext);
        }
        catch (GatewayInvalidQueryException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.BadRequest(ex.ErrorCode, "The semantic metric query parameters are invalid.", httpContext);
        }
        catch (KeyNotFoundException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "METRIC_NOT_FOUND");
            return SafeErrorResults.Problem(StatusCodes.Status404NotFound, "METRIC_NOT_FOUND", "One or more requested metrics were not found in the semantic catalog.", httpContext);
        }
        catch (GatewaySecurityException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, ex.ErrorCode);
            return SafeErrorResults.Problem(StatusCodes.Status403Forbidden, ex.ErrorCode, "Access to the requested semantic metric was denied by governance policy.", httpContext);
        }
        catch (ArgumentException ex)
        {
            SafeErrorResults.LogFailure(httpContext, ex, "INVALID_METRIC_QUERY");
            return SafeErrorResults.BadRequest("INVALID_METRIC_QUERY", "The semantic metric query parameters are invalid.", httpContext);
        }
    }

    internal static async Task<IResult> HandleListMetricsAsync(
        HttpContext httpContext,
        ISemanticCatalogService semanticCatalog,
        SemanticTenancy semanticTenancy,
        string? dataset = null,
        CancellationToken ct = default)
    {
        // A1-3 / A1-6 (d): the caller's own partition and its own version only; no SID or no tenant standing sees an empty catalog.
        var tenant = semanticTenancy.ResolveCallerTenant(httpContext.User) is null ? (TenantId?)null : EndpointSecurity.GetRequestTenant(httpContext);
        IReadOnlyList<DbtMetric> metrics = tenant is { } t
            ? await semanticCatalog.ListMetricsAsync(string.IsNullOrWhiteSpace(dataset) ? null : dataset, t, ct).ConfigureAwait(false)
            : [];
        var version = tenant is { } vt
            ? await semanticCatalog.GetManifestVersionAsync(vt, ct).ConfigureAwait(false)
            : "1.0";
        return Results.Ok(new
        {
            manifestVersion = version,
            total = metrics.Count,
            metrics = SemanticMetricCatalogProjection.Project(metrics, httpContext.User)
        });
    }

    private static IReadOnlyList<TableOrderBy>? ParseOrderByList(IReadOnlyList<string>? rawOrderBy)
    {
        if (rawOrderBy is not { Count: > 0 })
        {
            return null;
        }

        var list = new List<TableOrderBy>(rawOrderBy.Count);
        foreach (var entry in rawOrderBy)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool desc = parts.Length > 1 && string.Equals(parts[1], "DESC", StringComparison.OrdinalIgnoreCase);
            list.Add(new TableOrderBy(parts[0], desc));
        }

        return list;
    }
}
